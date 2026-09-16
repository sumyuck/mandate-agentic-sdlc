using System.Text;
using Mandate.Core.Execution;
using Mandate.Persistence.Workspaces;

namespace Mandate.Persistence.Tests;

/// <summary>
/// The reader hands workspace contents to a stage whose prompt a model writes, so the paths
/// it is asked for are untrusted input. These tests are mostly about what it refuses.
/// </summary>
public sealed class FileWorkspaceReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mandate-reader-" + Guid.NewGuid().ToString("N")[..8]);

    public FileWorkspaceReaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, ".git", "objects"));

        File.WriteAllText(Path.Combine(_root, "README.md"), "# Service");
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "class P { }");
        File.WriteAllText(Path.Combine(_root, ".git", "objects", "pack"), "binary-ish");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileWorkspaceReader Reader => new(_root);

    [Fact]
    public void The_tree_is_listed_with_forward_slashes_and_in_a_stable_order()
    {
        Reader.Files.ShouldBe(["README.md", "src/Program.cs"]);
    }

    [Fact]
    public void Gits_own_directory_is_not_part_of_the_tree_a_stage_reads()
    {
        // Not privacy — size and noise. A prompt built from the object store would be
        // megabytes of nothing a stage can use.
        Reader.Files.ShouldNotContain(path => path.StartsWith(".git", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_reads_back_exactly()
    {
        Reader.TryRead("src/Program.cs").ShouldBe("class P { }");
    }

    [Fact]
    public void A_file_that_is_not_there_reads_as_null_rather_than_throwing()
    {
        // A stage asking whether something exists yet is normal; an exception would turn a
        // question into a stage failure.
        Reader.TryRead("src/Missing.cs").ShouldBeNull();
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData(".git/objects/pack")]
    [InlineData("src/../../escape.txt")]
    [InlineData("")]
    public void A_path_that_tries_to_leave_the_workspace_is_refused(string path)
    {
        Reader.TryRead(path).ShouldBeNull();
    }

    [Fact]
    public void A_symlink_pointing_out_of_the_tree_is_refused()
    {
        // The string-level path rule cannot see through a link, which is why the resolved
        // absolute path is checked as well.
        string outside = Path.Combine(Path.GetTempPath(), "mandate-outside-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(outside, "not yours");

        try
        {
            File.CreateSymbolicLink(Path.Combine(_root, "link.txt"), outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Some filesystems and CI images refuse symlink creation. Nothing to assert.
            return;
        }

        try
        {
            Reader.TryRead("link.txt").ShouldBeNull();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void A_file_over_the_size_cap_is_not_read()
    {
        File.WriteAllText(
            Path.Combine(_root, "huge.txt"),
            new string('x', FileWorkspaceReader.MaxFileBytes + 1));

        Reader.Files.ShouldContain("huge.txt");
        Reader.TryRead("huge.txt").ShouldBeNull();
    }

    [Fact]
    public void A_file_that_is_not_valid_text_is_not_read()
    {
        // A binary that happens to decode reaches a prompt as plausible-looking nonsense,
        // which is worse than a file the stage knows it could not see.
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [0xFF, 0xFE, 0x00, 0x80, 0x41]);

        Reader.TryRead("blob.bin").ShouldBeNull();
    }

    [Fact]
    public void A_reader_over_a_directory_that_does_not_exist_is_simply_empty()
    {
        new FileWorkspaceReader(Path.Combine(_root, "nope")).Files.ShouldBeEmpty();
    }

    [Fact]
    public void Unicode_content_survives_the_round_trip()
    {
        File.WriteAllText(Path.Combine(_root, "unicode.md"), "héllo — ünicode ✓", Encoding.UTF8);

        Reader.TryRead("unicode.md").ShouldBe("héllo — ünicode ✓");
    }

    [Fact]
    public void The_empty_reader_reports_nothing_and_reads_nothing()
    {
        IWorkspaceReader.Empty.Files.ShouldBeEmpty();
        IWorkspaceReader.Empty.TryRead("README.md").ShouldBeNull();
    }
}
