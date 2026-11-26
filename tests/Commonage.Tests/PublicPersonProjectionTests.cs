using System.Reflection;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>
/// What visitors may be told about a person is what the projection selects: the record has no field for the note, the sort name,
/// when the choice was made or the internal id, and the query does not read the columns that hold them (plan M30, test 6).
/// </summary>
[Collection(nameof(AppCollection))]
public class PublicPersonProjectionTests(CommonageAppFactory app)
{
    private static readonly string[] NeverPublic = ["CuratorNote", "DisclosureChangedAt", "SortName"];

    [Fact]
    public async Task The_sql_of_the_public_person_projection_never_selects_the_note_the_choice_date_or_the_sort_name()
    {
        var sql = await app.WithDbAsync(db => Task.FromResult(VisiblePeople.Projection(db.VisiblePeople, "abcdefghjkmn").ToQueryString()));

        Assert.Contains("\"Biography\"", sql); // the query is the real one, not an empty string
        foreach (var column in NeverPublic)
            Assert.DoesNotContain(column, sql);
    }

    [Fact]
    public void The_public_person_records_have_no_field_for_what_stays_with_curators()
    {
        var types = new[] { typeof(PersonView), typeof(VisiblePeople.PersonRow), typeof(PublicPersonName) };
        var members = types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)).Select(p => p.Name).ToList();

        Assert.NotEmpty(members);
        foreach (var forbidden in NeverPublic.Append("Id"))
            Assert.DoesNotContain(forbidden, members);
    }
}
