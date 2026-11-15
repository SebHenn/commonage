namespace Commonage.Web.Domain;

/// <summary>The descriptive fields whose earlier text is kept. Stored by name. Never a contributor's details or the internal notes.</summary>
public enum RevisionField
{
    Title,
    Description,
    Date,
    Credit,
    OriginalLocation,
    ReferenceNumber,
}

/// <summary>
/// What one field said before a curator or helper changed it. Only changed fields get a row. The row goes with its item
/// (and with the bin's 30 days), and holds no contributor details: the fields it can hold are listed in <see cref="RevisionField"/>.
/// </summary>
public class ItemRevision
{
    public long Id { get; set; }
    public int ArchiveItemId { get; set; }
    public ArchiveItem ArchiveItem { get; set; } = null!;
    public DateTime At { get; set; }

    /// <summary>The account that changed it, and its name at the time, as in the activity log.</summary>
    public string? UserId { get; set; }
    public string? UserName { get; set; }

    public RevisionField Field { get; set; }

    /// <summary>The text before the change; null if the field was empty.</summary>
    public string? OldValue { get; set; }
}

/// <summary>Reads and writes the fields that keep their earlier text.</summary>
public static class RevisionFields
{
    public static string? Get(ArchiveItem item, RevisionField field) => field switch
    {
        RevisionField.Title => item.Title,
        RevisionField.Description => item.Description,
        RevisionField.Date => item.DateText,
        RevisionField.Credit => item.Credit,
        RevisionField.OriginalLocation => item.OriginalLocation,
        RevisionField.ReferenceNumber => item.ReferenceNumber,
        _ => null,
    };

    /// <summary>Puts a text back. False if the item would not accept it (an empty title, a date that no longer reads).</summary>
    public static bool TrySet(ArchiveItem item, RevisionField field, string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value;
        switch (field)
        {
            case RevisionField.Title:
                if (value is null)
                    return false;
                item.Title = value;
                return true;
            case RevisionField.Description: item.Description = value; return true;
            case RevisionField.Date: return item.TrySetDate(value);
            case RevisionField.Credit: item.Credit = value; return true;
            case RevisionField.OriginalLocation: item.OriginalLocation = value; return true;
            case RevisionField.ReferenceNumber: item.ReferenceNumber = value; return true;
            default: return false;
        }
    }

    public static bool Differ(string? before, string? after) => (before ?? "") != (after ?? "");
}
