namespace SoftPrint.Application.Abstractions;

public interface IFolderOperations
{
    bool CanBrowse { get; }
    string Open(string path);
    string? Browse(string startPath);
}
