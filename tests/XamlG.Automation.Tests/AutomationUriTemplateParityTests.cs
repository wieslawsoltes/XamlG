using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AutomationUriTemplateParityTests
{
    [Theory]
    [InlineData("xamlg://ui/{id}", "xamlg://ui/card", "card")]
    [InlineData("xamlg://ui/{id}", "xamlg://ui/a%2Fb%3Fc%23d", "a/b?c#d")]
    [InlineData("xamlg://ui/{id}", "xamlg://ui/%E2%9C%93", "✓")]
    [InlineData("xamlg://ui/{id}.json", "xamlg://ui/card.json", "card")]
    [InlineData("xamlg://ui/({id})[v1]+", "xamlg://ui/(card)[v1]+", "card")]
    public void Captures_encoded_values_and_treats_literals_ordinally(string template, string uri, string expected)
        => Assert.Equal(expected, AutomationUriTemplate.Match(template, uri)!["id"]);

    [Theory]
    [InlineData("xamlg://ui/")]
    [InlineData("xamlg://ui/a/b")]
    [InlineData("xamlg://ui/a?b")]
    [InlineData("xamlg://ui/a#b")]
    [InlineData("XAMLG://ui/a")]
    public void Captures_are_nonempty_single_segments(string uri)
        => Assert.Null(AutomationUriTemplate.Match("xamlg://ui/{id}", uri));

    [Fact]
    public void Adjacent_and_separated_variables_preserve_greedy_capture()
    {
        var adjacent = AutomationUriTemplate.Match("{a}{b}", "abcd")!;
        Assert.Equal("abc", adjacent["a"]); Assert.Equal("d", adjacent["b"]);
        var separated = AutomationUriTemplate.Match("{a}-{b}", "one-two-three")!;
        Assert.Equal("one-two", separated["a"]); Assert.Equal("three", separated["b"]);
        var suffix = AutomationUriTemplate.Match("{a}xy{b}end", "beforexyafterend")!;
        Assert.Equal("before", suffix["a"]); Assert.Equal("after", suffix["b"]);
    }

    [Fact]
    public void Exact_literal_matches_do_not_accept_trailing_newlines()
    {
        Assert.Empty(AutomationUriTemplate.Match("", "")!);
        Assert.Empty(AutomationUriTemplate.Match("xamlg://literal", "xamlg://literal")!);
        Assert.Null(AutomationUriTemplate.Match("xamlg://literal", "xamlg://literal\n"));
        Assert.Null(AutomationUriTemplate.Match("{id}.json", "a.json\n"));
    }

    [Fact]
    public void Invalid_variable_syntax_remains_literal()
    {
        foreach (var literal in new[] { "{_id}", "{1id}", "{id-with-dash}", "{id", "}" })
            Assert.Empty(AutomationUriTemplate.Match(literal, literal)!);
        Assert.Equal("value", AutomationUriTemplate.Match("{{id}}", "{value}")!["id"]);
    }

    [Fact]
    public void Duplicate_names_and_resource_limits_fail_closed()
    {
        Assert.Null(AutomationUriTemplate.Match("{id}/{id}", "a/b"));
        Assert.Null(AutomationUriTemplate.Match("{a}{b}{c}{d}{e}{f}{g}{h}{i}", "123456789"));
        Assert.Null(AutomationUriTemplate.Match(new string('x', 2049), "x"));
        Assert.Null(AutomationUriTemplate.Match("{id}", new string('x', 4097)));
        Assert.Equal(4096, AutomationUriTemplate.Match("{id}", new string('x', 4096))!["id"].Length);
    }

    [Fact]
    public void Adversarial_multi_variable_suffixes_are_bounded_without_timeout_or_backtracking()
    {
        var uri = new string('x', 4096);
        Assert.Null(AutomationUriTemplate.Match("{a}{b}{c}{d}{e}{f}{g}{h}absent", uri));
        var values = AutomationUriTemplate.Match("{a}{b}{c}{d}{e}{f}{g}{h}", uri)!;
        Assert.Equal(4089, values["a"].Length);
        Assert.All(values.Where(pair => pair.Key != "a"), pair => Assert.Equal("x", pair.Value));
    }
}
