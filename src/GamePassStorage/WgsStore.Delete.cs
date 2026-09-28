namespace GamePassStorage;

/// <summary>What deleting a container does to the index.</summary>
public enum WgsDeleteAction
{
    /// <summary>The container was never uploaded (no ETag): its index entry is removed and its files cleared.</summary>
    RemoveFromIndex,
    /// <summary>The cloud knows the container (it has an ETag): it becomes a Deleted tombstone that keeps the ETag.</summary>
    MarkDeleted,
    /// <summary>Already a tombstone; nothing to do.</summary>
    AlreadyDeleted,
}

/// <summary>A preview of a delete. Nothing is touched to produce it.</summary>
public sealed record WgsDeletePlan(string ContainerName, WgsDeleteAction Action, IReadOnlyList<string> Steps,
    WgsWriteAssessment Assessment);

// Deleting a container.
//
// Rule (documented, not verified against a live sync): the index state field tells whether the cloud
// knows a container. State 5 (Created) with an empty ETag means "made locally, never uploaded"; state
// 3 (Deleted) is "a tombstone kept so the deletion can reach the cloud" (docs/wgs-format.md, mapping
// after libNOM.io). So:
//   * no ETag  -> nothing to tell the cloud: remove the entry from the index (and clear its files);
//   * has ETag -> keep the entry, set state 3, keep the ETag untouched so the service can match the
//                 deletion to the version it issued. The blob files are left in place.
// A tombstone is not repaired back to life by RepairRecoveredManifests and does not block writes to
// other containers; writing to the tombstoned container itself is refused.
public sealed partial class WgsStore
{
    /// <summary>Previews <see cref="DeleteContainer"/>. Touches nothing.</summary>
    public WgsDeletePlan PlanDelete(WgsContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var action = ActionFor(container);
        var steps = action switch
        {
            WgsDeleteAction.RemoveFromIndex => new[]
            {
                $"remove '{container.Name}' from {IndexFileName} (it was never uploaded, so the cloud has nothing to delete)",
                "clear the container's manifest and blob files",
            },
            WgsDeleteAction.MarkDeleted => new[]
            {
                $"mark '{container.Name}' Deleted in {IndexFileName}, keeping its ETag so the deletion syncs",
                "leave its files in place",
            },
            _ => new[] { $"'{container.Name}' is already deleted; nothing to do" },
        };
        return new WgsDeletePlan(container.Name, action, steps, AssessWrite());
    }

    private static WgsDeleteAction ActionFor(WgsContainer c)
        => c.RawState == (uint)WgsEntryState.Deleted && !string.IsNullOrEmpty(c.Etag) ? WgsDeleteAction.AlreadyDeleted
            : string.IsNullOrEmpty(c.Etag) ? WgsDeleteAction.RemoveFromIndex
            : WgsDeleteAction.MarkDeleted;

    /// <summary>
    /// Deletes a container by the rule above. Returns the tombstone, or null when the entry was removed
    /// outright. Throws when the write gate refuses.
    /// </summary>
    public WgsContainer? DeleteContainer(WgsContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var position = _containers.IndexOf(container);
        if (position < 0) throw new InvalidOperationException($"'{container.Name}' is not a container of this store.");
        EnsureWritable();

        switch (ActionFor(container))
        {
            case WgsDeleteAction.AlreadyDeleted:
                return container;

            case WgsDeleteAction.RemoveFromIndex:
            {
                _containers.RemoveAt(position);
                try
                {
                    WriteIndex();
                }
                catch
                {
                    _containers.Insert(position, container);
                    throw;
                }
                // Only after the index no longer names the folder is it safe to clear it.
                var folder = Path.Combine(_root, container.FolderName);
                try
                {
                    RemoveBestEffort(_fs.DirectoryExists(folder) ? _fs.EnumerateFiles(folder).ToList() : []);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _options.Log.Warn($"Could not clear the folder of deleted container '{container.Name}': {ex.Message}");
                }
                _options.Log.Info($"wgs: removed never-uploaded container '{container.Name}' from the index.");
                return null;
            }

            default:
            {
                var saved = (container.State, container.RawState, container.FileTime);
                container.State = WgsEntryState.Deleted;
                container.RawState = (uint)WgsEntryState.Deleted;
                container.FileTime = NowEntryFileTime();
                try
                {
                    WriteIndex();
                }
                catch
                {
                    (container.State, container.RawState, container.FileTime) = saved;
                    throw;
                }
                _options.Log.Info($"wgs: marked container '{container.Name}' Deleted (ETag kept) so the deletion can sync.");
                return container;
            }
        }
    }

    /// <summary>Typed-result form of <see cref="DeleteContainer"/>. <c>Container</c> is null when the entry was removed.</summary>
    public WgsCommitResult TryDeleteContainer(WgsContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return Commit(container.Name, () => DeleteContainer(container));
    }
}
