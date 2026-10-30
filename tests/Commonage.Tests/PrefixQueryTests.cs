using Commonage.Web.Search;

namespace Commonage.Tests;

public class PrefixQueryTests
{
    [Theory]
    [InlineData("Mill  school", "mill:* & school:*")]
    [InlineData("Mühle, Mühle!", "mühle:*")]
    [InlineData("o'brien & (x | y)", "o:* & brien:* & x:* & y:*")]
    [InlineData("हिन्दी भाषा", "हिन्दी:* & भाषा:*")]
    [InlineData("ประวัติ", "ประวัติ:*")]
    [InlineData("東京都", "東京都:*")]
    public void Keeps_whole_words_and_drops_everything_else(string text, string expected)
    {
        Assert.Equal(expected, ItemSearch.ToPrefixQuery(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("&|!:*()")]
    [InlineData("\u0301\u093f")]
    public void Returns_null_without_words(string? text)
    {
        Assert.Null(ItemSearch.ToPrefixQuery(text));
    }
}
