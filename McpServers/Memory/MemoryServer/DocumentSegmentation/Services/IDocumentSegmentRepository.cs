using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.Infrastructure;
using MemoryServer.Models;

namespace MemoryServer.DocumentSegmentation.Services;

/// <summary>
/// Repository interface for document segment operations using the Database Session Pattern.
/// </summary>
public interface IDocumentSegmentRepository
{
  /// <summary>
  /// Stores document segments in the database.
  /// </summary>
  /// <param name="session">Database session for transaction management</param>
  /// <param name="segments">List of segments to store</param>
  /// <param name="parentDocumentId">ID of the parent document</param>
  /// <param name="sessionContext">Session context for isolation</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of generated segment IDs</returns>
  Task<List<int>> StoreSegmentsAsync(
    ISqliteSession session,
    List<DocumentSegment> segments,
    int parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Retrieves all segments for a parent document.
  /// </summary>
  /// <param name="session">Database session for transaction management</param>
  /// <param name="parentDocumentId">ID of the parent document</param>
  /// <param name="sessionContext">Session context for isolation</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of document segments ordered by sequence</returns>
  Task<List<DocumentSegment>> GetDocumentSegmentsAsync(
    ISqliteSession session,
    int parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Stores segment relationships in the database.
  /// </summary>
  /// <param name="session">Database session for transaction management</param>
  /// <param name="relationships">List of segment relationships to store</param>
  /// <param name="sessionContext">Session context for isolation</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Number of relationships stored</returns>
  Task<int> StoreSegmentRelationshipsAsync(
    ISqliteSession session,
    List<SegmentRelationship> relationships,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Retrieves segment relationships for a document.
  /// </summary>
  /// <param name="session">Database session for transaction management</param>
  /// <param name="parentDocumentId">ID of the parent document</param>
  /// <param name="sessionContext">Session context for isolation</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of segment relationships</returns>
  Task<List<SegmentRelationship>> GetSegmentRelationshipsAsync(
    ISqliteSession session,
    int parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Deletes all segments and relationships for a parent document.
  /// </summary>
  /// <param name="session">Database session for transaction management</param>
  /// <param name="parentDocumentId">ID of the parent document</param>
  /// <param name="sessionContext">Session context for isolation</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Number of segments deleted</returns>
  Task<int> DeleteDocumentSegmentsAsync(
    ISqliteSession session,
    int parentDocumentId,
    SessionContext sessionContext,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Searches segments using full-text search.
  /// </summary>
  /// <param name="session">Database session for transaction management</param>
  /// <param name="query">Search query</param>
  /// <param name="sessionContext">Session context for isolation</param>
  /// <param name="limit">Maximum number of results</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of matching segments with relevance scores</returns>
  Task<List<DocumentSegment>> SearchSegmentsAsync(
    ISqliteSession session,
    string query,
    SessionContext sessionContext,
    int limit = 10,
    CancellationToken cancellationToken = default);
}
