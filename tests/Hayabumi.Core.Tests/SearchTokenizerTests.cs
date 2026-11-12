using Hayabumi.Core.Search;

namespace Hayabumi.Core.Tests;

public class SearchTokenizerTests
{
    [Fact]
    public void Japanese_text_is_split_into_bigrams_with_trailing_unigram()
    {
        Assert.Equal("会議 議室 室の の予 予約 約", SearchTokenizer.ToIndexText("会議室の予約"));
    }

    [Fact]
    public void Whitespace_separates_runs()
    {
        Assert.Equal("会議 議の の 室", SearchTokenizer.ToIndexText("会議の 室"));
    }

    [Fact]
    public void Latin_words_and_numbers_are_kept_as_words()
    {
        Assert.Equal("invoice 2026 11 請求 求書 書", SearchTokenizer.ToIndexText("Invoice 2026-11 請求書"));
    }

    [Fact]
    public void Full_width_and_half_width_are_normalized()
    {
        Assert.Equal("abc カナ ナ", SearchTokenizer.ToIndexText("ＡＢＣ ｶﾅ"));
    }

    [Fact]
    public void Query_becomes_prefix_phrases_joined_by_AND()
    {
        Assert.Equal("\"会議 議室\" * AND \"abc\" *", SearchTokenizer.ToMatchExpression("会議室  ABC"));
    }

    [Fact]
    public void Query_mixing_japanese_and_latin_keeps_positions_aligned_with_index()
    {
        Assert.Equal("\"送信 信者 者 42\" *", SearchTokenizer.ToMatchExpression("送信者42"));
        Assert.Equal("送信 信者 者 42", SearchTokenizer.ToIndexText("送信者42"));
    }

    [Fact]
    public void Single_character_query_becomes_prefix()
    {
        Assert.Equal("\"議\" *", SearchTokenizer.ToMatchExpression("議"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ---")]
    public void Empty_query_returns_null(string query)
    {
        Assert.Null(SearchTokenizer.ToMatchExpression(query));
    }

    [Fact]
    public void Quotes_and_operators_in_query_are_not_injected()
    {
        var expression = SearchTokenizer.ToMatchExpression("a\" OR \"b NEAR(x)");
        Assert.Equal("\"a\" * AND \"or\" * AND \"b\" * AND \"near x\" *", expression);
    }

    [Fact]
    public void Long_text_is_truncated()
    {
        var text = new string('あ', 100);
        var index = SearchTokenizer.ToIndexText(text, maxLength: 10);
        Assert.Equal(10, index.Split(' ').Length);
    }
}
