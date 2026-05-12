using Agency.GraphRAG.Code.ChangeDetector;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Hydration;

/// <summary>
/// Coordinates incremental hydration by applying file mutations, rerunning definition writes, and resolving impacted files.
/// </summary>
public sealed class IncrementalHydrator(
    IGraphStore graphStore,
    Func<Phase1WriteRequest, CancellationToken, Task> writePhase1Async,
    Func<Guid, IReadOnlyList<ExternalPackage>, CancellationToken, Task> resolvePhase2Async,
    Func<string, CancellationToken, Task<Guid?>> lookupFileIdByPathAsync,
    Func<ChangeSet, CancellationToken, Task<IReadOnlyList<Guid>>> resolveReverseAffectedFileIdsAsync)
{
    /// <summary>
    /// Applies the supplied change set using parsed-file write requests and package scopes.
    /// </summary>
    /// <param name="changeSet">The detected repository change set.</param>
    /// <param name="writeRequestsByPath">The parsed-file requests keyed by repository-relative path.</param>
    /// <param name="packagesByFileId">External packages in scope keyed by file identifier.</param>
    /// <param name="onProgress">Optional callback for sub-step progress messages.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task HydrateAsync(
        ChangeSet changeSet,
        IReadOnlyDictionary<string, Phase1WriteRequest> writeRequestsByPath,
        IReadOnlyDictionary<Guid, IReadOnlyList<ExternalPackage>> packagesByFileId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(writeRequestsByPath);
        ArgumentNullException.ThrowIfNull(packagesByFileId);

        if (changeSet.DeletedFiles.Count > 0)
        {
            onProgress?.Invoke($"  Deleting {changeSet.DeletedFiles.Count} file(s)...");
            foreach (string deletedPath in changeSet.DeletedFiles)
            {
                Guid? fileId = await lookupFileIdByPathAsync(deletedPath, cancellationToken).ConfigureAwait(false);
                if (fileId.HasValue)
                {
                    await graphStore.DeleteFileAsync(fileId.Value, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (changeSet.RenamedFiles.Count > 0)
        {
            onProgress?.Invoke($"  Renaming {changeSet.RenamedFiles.Count} file(s)...");
            foreach (RenamedFileChange rename in changeSet.RenamedFiles)
            {
                Guid? fileId = await lookupFileIdByPathAsync(rename.OldPath, cancellationToken).ConfigureAwait(false);
                if (fileId.HasValue)
                {
                    await graphStore.RenameFileAsync(fileId.Value, rename.NewPath, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        List<(string Path, Phase1WriteRequest Request)> phase1Work = [];
        foreach (string path in changeSet.AddedFiles
            .Concat(changeSet.ModifiedFiles.Select(static c => c.Path))
            .Concat(changeSet.RenamedFiles.Select(static c => c.NewPath))
            .Distinct(StringComparer.Ordinal))
        {
            if (writeRequestsByPath.TryGetValue(path, out Phase1WriteRequest? request))
            {
                phase1Work.Add((path, request));
            }
        }

        if (phase1Work.Count > 0)
        {
            onProgress?.Invoke($"  Writing {phase1Work.Count} file(s) (phase 1)...");
        }

        List<Guid> filesToResolve = [];
        for (int i = 0; i < phase1Work.Count; i++)
        {
            (string path, Phase1WriteRequest request) = phase1Work[i];
            onProgress?.Invoke($"  [phase1 {i + 1}/{phase1Work.Count}] {path}");
            await writePhase1Async(request, cancellationToken).ConfigureAwait(false);
            filesToResolve.Add(request.File.Id);
        }

        IReadOnlyList<Guid> reverseAffected = await resolveReverseAffectedFileIdsAsync(changeSet, cancellationToken).ConfigureAwait(false);
        List<Guid> phase2Ids = filesToResolve.Concat(reverseAffected).Distinct().ToList();

        if (phase2Ids.Count > 0)
        {
            onProgress?.Invoke($"  Resolving references for {phase2Ids.Count} file(s) (phase 2)...");
        }

        Dictionary<Guid, string> filePathById = phase1Work.ToDictionary(
            static pair => pair.Request.File.Id,
            static pair => pair.Path);

        for (int i = 0; i < phase2Ids.Count; i++)
        {
            Guid fileId = phase2Ids[i];
            string label = filePathById.TryGetValue(fileId, out string? p) ? p : fileId.ToString("D");
            onProgress?.Invoke($"  [phase2 {i + 1}/{phase2Ids.Count}] {label}");
            await resolvePhase2Async(
                fileId,
                packagesByFileId.TryGetValue(fileId, out IReadOnlyList<ExternalPackage>? packages) ? packages : [],
                cancellationToken).ConfigureAwait(false);
        }
    }
}
