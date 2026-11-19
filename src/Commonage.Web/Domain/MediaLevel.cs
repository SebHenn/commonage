namespace Commonage.Web.Domain;

/// <summary>What visitors get of a published item. Stored as a number, so new levels go at the end.</summary>
public enum MediaLevel
{
    /// <summary>The record and everything attached to it: photos, pages, recordings.</summary>
    Everything = 0,

    /// <summary>
    /// Only the record: title, description, date, place and credit. No picture, page image or recording anywhere, and nothing
    /// said about what is in them (captions, alt text, the text of PDFs). The description stays, also a recording's transcript.
    /// </summary>
    RecordOnly = 1,
}
