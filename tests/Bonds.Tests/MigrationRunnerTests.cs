using Bonds.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests;

/// <summary>
/// Тесты <see cref="MigrationRunner.SplitSqlStatements"/> (footgun из аудита расчётов, п.3):
/// наивный сплит SQL-файла по символу ';' не вырезал '--'-комментарии до конца строки, поэтому
/// точка-с-запятой внутри текста комментария ломала разбиение на statements — воспроизведено на
/// реальной миграции 010_purge_intraday_quotes.sql (предупреждающий комментарий о самой этой
/// проблеме содержит символ ';' в тексте).
/// </summary>
public class MigrationRunnerTests
{
    [Fact]
    public void SplitSqlStatements_SemicolonInsideLineComment_DoesNotSplitThere_OneStatement()
    {
        const string sql = "-- note: this comment contains a semicolon; right here\nDELETE FROM intraday_quotes;";

        var statements = MigrationRunner.SplitSqlStatements(sql);

        statements.Should().ContainSingle().Which.Trim().Should().Be("DELETE FROM intraday_quotes");
    }

    [Fact]
    public void SplitSqlStatements_MultipleStatementsWithCommentsBetween_SplitsCorrectly()
    {
        const string sql = """
            -- first statement; note the semicolon in this comment
            CREATE TABLE foo (id INT);
            -- second statement; another semicolon in a comment
            CREATE TABLE bar (id INT);
            """;

        var statements = MigrationRunner.SplitSqlStatements(sql);

        statements.Should().HaveCount(2);
        statements[0].Should().Contain("CREATE TABLE foo");
        statements[1].Should().Contain("CREATE TABLE bar");
        statements.Should().OnlyContain(s => !s.Contains("--"), "текст комментариев не должен просачиваться в исполняемый statement");
    }

    [Fact]
    public void SplitSqlStatements_NoComments_BehavesAsPlainSemicolonSplit()
    {
        const string sql = "CREATE TABLE a (id INT); CREATE TABLE b (id INT);";

        var statements = MigrationRunner.SplitSqlStatements(sql);

        statements.Should().HaveCount(2);
    }

    [Fact]
    public void SplitSqlStatements_CommentOnlyLine_ProducesNoEmptyStatement()
    {
        const string sql = "-- just a comment, no sql at all\n-- another comment; with a semicolon";

        var statements = MigrationRunner.SplitSqlStatements(sql);

        statements.Should().BeEmpty();
    }

    [Fact]
    public void SplitSqlStatements_TrailingLineCommentAfterStatement_DoesNotBreakSplit()
    {
        const string sql = "DELETE FROM foo; -- cleanup; done";

        var statements = MigrationRunner.SplitSqlStatements(sql);

        statements.Should().ContainSingle().Which.Trim().Should().Be("DELETE FROM foo");
    }
}
