using CodexBackup.Infrastructure.Windows.Discovery;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class ProjectGitStatusParserTests
{
    [Fact]
    public void Parse_ReadsBranchRemoteDetailsAndWorkingTreeCounts()
    {
        const string output = """
                              ## main...origin/main [ahead 2, behind 1]
                               M src/changed.cs
                              ?? notes.txt
                              """;

        var status = ProjectGitStatusParser.Parse(output, hasRemote: true);

        Assert.Equal("main", status.BranchName);
        Assert.Equal("ahead 2, behind 1", status.AheadBehindSummary);
        Assert.True(status.HasRemote);
        Assert.Equal(1, status.ChangedTrackedFileCount);
        Assert.Equal(1, status.UntrackedFileCount);
        Assert.True(status.HasLocalChanges);
    }

    [Fact]
    public void Parse_RecognizesDetachedHead()
    {
        var status = ProjectGitStatusParser.Parse("## HEAD (no branch)\n", hasRemote: false);

        Assert.True(status.IsDetachedHead);
        Assert.Null(status.BranchName);
        Assert.True(status.IsClean);
    }
}
