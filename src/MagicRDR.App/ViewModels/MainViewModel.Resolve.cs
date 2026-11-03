using MagicRDR.Core;

namespace MagicRDR.App.ViewModels;

/// <summary>
/// Automatic naming of hashed entries. Every time the list is refreshed (<see cref="ScheduleHashResolution"/>) it checks whether
/// the open archive still has entries shown as <c>0x…</c> and whether anything changed since the last attempt (another archive,
/// or new names in the dictionary). If so it runs <see cref="RdrHashResolver"/> as a normal cancellable operation, adds the names
/// it finds to the dictionary, remembers them in app storage and refreshes the list, so the real names appear by themselves.
/// It never starts while another operation is running; it waits until that one has finished.
/// </summary>
public partial class MainViewModel
{
    private bool _resolveHooked;
    private bool _resolveQueued;
    private bool _resolveWaiting;
    private Rpf6Archive? _resolveArchive;
    private int _resolveNameCount = -1;
    private int _resolveSignature;

    /// <summary>Called after every list refresh. Cheap when nothing changed.</summary>
    private void ScheduleHashResolution()
    {
        Rpf6Archive? archive = _archive;
        if (archive == null || _resolveQueued)
        {
            return;
        }

        if (ReferenceEquals(archive, _resolveArchive) && _names.Count == _resolveNameCount)
        {
            return;   // same archive, same names: already tried
        }

        EnsureResolveHook();
        _resolveQueued = true;
        _ = StartHashResolutionLaterAsync();
    }

    /// <summary>When a running operation ends, a waiting resolve gets its turn.</summary>
    private void EnsureResolveHook()
    {
        if (_resolveHooked)
        {
            return;
        }

        _resolveHooked = true;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsLoading) && !IsLoading && _resolveWaiting)
            {
                _resolveWaiting = false;
                ScheduleHashResolution();
            }
        };
    }

    private async Task StartHashResolutionLaterAsync()
    {
        try
        {
            // Let the operation that refreshed the list finish unwinding first.
            await Task.Delay(250);
            _resolveQueued = false;
            await ResolveHashesAsync();
        }
        catch (Exception)
        {
            _resolveQueued = false;   // automatic naming must never crash the app
        }
    }

    private async Task ResolveHashesAsync()
    {
        Rpf6Archive? archive = _archive;
        if (archive == null)
        {
            return;
        }

        if (IsLoading)
        {
            _resolveWaiting = true;   // tried again when the running operation ends
            return;
        }

        List<RdrHashTarget> targets = CollectHashTargets(archive);
        int signature = SignatureOf(targets);

        _resolveArchive = archive;
        _resolveNameCount = _names.Count;
        if (targets.Count == 0 || signature == _resolveSignature)
        {
            _resolveSignature = signature;   // nothing to name, or the same entries and names as the last attempt
            return;
        }

        var localEntries = new List<RdrKnownEntry>();
        foreach (RpfNode node in archive.EnumerateAll())
        {
            if (!node.IsRoot && _names.Contains(node.NameHash))
            {
                localEntries.Add(new RdrKnownEntry(node.Name, node.IsDirectory, node.IsResource, (byte)(node.AsFile?.ResourceType ?? 0)));
            }
        }

        string[] knownNames = _names.Names.ToArray();

        await RunAsync($"Resolving {targets.Count:N0} hashed names…", async ct =>
        {
            var progress = new Progress<double>(fraction =>
            {
                if (IsLoading)
                {
                    ProgressValue = fraction;
                }
            });

            RdrResolveResult result;
            try
            {
                result = await Task.Run(
                    () => RdrHashResolver.Resolve(targets, knownNames, localEntries, null, progress, ct),
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusMessage = $"Automatic name resolving stopped: {ex.Message}";
                return;
            }

            // Names found before a Cancel are kept, like in "Find names".
            int added = 0;
            foreach (RdrResolvedName found in result.Found)
            {
                if (_names.Add(found.Name))
                {
                    added++;
                }
            }

            _resolveArchive = archive;
            _resolveNameCount = _names.Count;
            _resolveSignature = SignatureOf(targets);

            if (added > 0)
            {
                await SaveUserNamesAsync();
                AfterNamesChanged();   // the list is rebuilt, so the real names replace the hashes right away
            }

            int remaining = targets.Count(t => !_names.Contains(t.Hash));
            StatusMessage = added > 0
                ? $"Resolved {added:N0} hashed names automatically. {remaining:N0} are still unresolved."
                : result.Cancelled
                    ? "Cancelled. No names were found yet."
                    : $"No new names could be generated for the {targets.Count:N0} hashed entries.";
        });
    }

    /// <summary>The entries of the whole archive that still have no name (one per hash and kind).</summary>
    private List<RdrHashTarget> CollectHashTargets(Rpf6Archive archive)
    {
        var targets = new List<RdrHashTarget>();
        var seen = new HashSet<(uint, bool, bool, byte)>();

        foreach (RpfNode node in archive.EnumerateAll())
        {
            if (node.IsRoot || _names.Contains(node.NameHash))
            {
                continue;
            }

            var target = new RdrHashTarget(node.NameHash, node.IsDirectory, node.IsResource, (byte)(node.AsFile?.ResourceType ?? 0));
            if (seen.Add((target.Hash, target.IsDirectory, target.IsResource, target.ResourceType)))
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    /// <summary>Identifies "these still-unnamed entries with this many known names", so an identical situation is not searched twice (for example after Save RPF).</summary>
    private int SignatureOf(IReadOnlyList<RdrHashTarget> targets)
    {
        uint xor = 0;
        uint sum = 0;
        int open = 0;

        foreach (RdrHashTarget target in targets)
        {
            if (_names.Contains(target.Hash))
            {
                continue;
            }

            open++;
            xor ^= target.Hash;
            unchecked
            {
                sum += target.Hash;
            }
        }

        return HashCode.Combine(_names.Count, open, xor, sum);
    }
}
