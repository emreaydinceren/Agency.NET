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
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task HydrateAsync(
        ChangeSet changeSet,
        IReadOnlyDictionary<string, Phase1WriteRequest> writeRequestsByPath,
        IReadOnlyDictionary<Guid, IReadOnlyList<ExternalPackage>> packagesByFileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(writeRequestsByPath);
        ArgumentNullException.ThrowIfNull(packagesByFileId);

        foreach (string deletedPath in changeSet.DeletedFiles)
        {
            Guid? fileId = await lookupFileIdByPathAsync(deletedPath, cancellationToken).ConfigureAwait(false);
            if (fileId.HasValue)
            {
                await graphStore.DeleteFileAsync(fileId.Value, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (RenamedFileChange rename in changeSet.RenamedFiles)
        {
            Guid? fileId = await lookupFileIdByPathAsync(rename.OldPath, cancellationToken).ConfigureAwait(false);
            if (fileId.HasValue)
            {
                await graphStore.RenameFileAsync(fileId.Value, rename.NewPath, cancellationToken).ConfigureAwait(false);
            }
        }

        List<Guid> filesToResolve = [];
        foreach (string path in changeSet.AddedFiles.Concat(changeSet.ModifiedFiles.Select(static change => change.Path)).Concat(changeSet.RenamedFiles.Select(static change => change.NewPath)).Distinct(StringComparer.Ordinal))
        {
            if (!writeRequestsByPath.TryGetValue(path, out Phase1WriteRequest? request))
            {
                continue;
            }

            await writePhase1Async(request, cancellationToken).ConfigureAwait(false);
            filesToResolve.Add(request.File.Id);
        }

        IReadOnlyList<Guid> reverseAffected = await resolveReverseAffectedFileIdsAsync(changeSet, cancellationToken).ConfigureAwait(false);
        foreach (Guid fileId in filesToResolve.Concat(reverseAffected).Distinct())
        {
            await resolvePhase2Async(
                fileId,
                packagesByFileId.TryGetValue(fileId, out IReadOnlyList<ExternalPackage>? packages) ? packages : [],
                cancellationToken).ConfigureAwait(false);
        }
    }
}
