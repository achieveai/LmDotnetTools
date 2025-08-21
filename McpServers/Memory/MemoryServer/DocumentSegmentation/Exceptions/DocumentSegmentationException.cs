namespace MemoryServer.DocumentSegmentation.Exceptions;

/// <summary>
/// Exception thrown when document segmentation operations fail.
/// </summary>
public class DocumentSegmentationException : Exception
{
    public DocumentSegmentationException() : base()
    {
    }

    public DocumentSegmentationException(string message) : base(message)
    {
    }

    public DocumentSegmentationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
