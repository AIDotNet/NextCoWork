using Ncw.Agent.Environments;

namespace Ncw.Agent.Environments.Tests;

/// <summary>移植自 2.x <c>environment/__tests__/paths.test.ts</c>,外加路径规则本身的边界。</summary>
public class PathTests
{
    private static PathGuard IdentityGuard(PathStyle style) => new(new FakeFs(p => p), style);

    [Fact]
    public async Task Windows_semantics_apply_even_on_a_posix_host()
    {
        var guard = IdentityGuard(PathStyle.Windows);
        var root = EnvPath.Windows(@"C:\work");

        var inside = await guard.ResolveAsync(root, "src/file.ts");
        Assert.Equal(@"C:\work\src\file.ts", inside.Absolute.Value);
        Assert.False(inside.Outside);

        var other = await guard.ResolveAsync(root, @"D:\other");
        Assert.Equal(@"D:\other", other.Absolute.Value);
        Assert.True(other.Outside);

        Assert.Equal("src/file.ts", guard.Display(root, inside.Absolute));
        Assert.Equal(".", guard.Display(root, root));

        foreach (var invalid in new[] { "D:relative", "D:", "/folder", @"\folder", @"\\?\C:\work", @"\\.\pipe\name" })
        {
            await Assert.ThrowsAsync<EnvironmentException>(async () => await guard.ResolveAsync(root, invalid));
            Assert.False(PathRules.IsAbsolute(PathStyle.Windows, invalid));
        }

        var unc = EnvPath.Windows(@"\\server\share\work");
        var uncInside = await guard.ResolveAsync(unc, "src/file.ts");
        Assert.Equal(@"\\server\share\work\src\file.ts", uncInside.Absolute.Value);
        Assert.False(uncInside.Outside);
    }

    [Fact]
    public void Sftp_paths_use_the_slash_drive_convention_on_windows_hosts()
    {
        Assert.Equal("/C:/Users/user/work", PathRules.ToSftpPath(PathStyle.Windows, @"C:\Users\user\work"));
        Assert.Equal(@"C:\Users\user\work", PathRules.FromSftpPath(PathStyle.Windows, "/C:/Users/user/work"));
        Assert.Equal(@"C:\", PathRules.FromSftpPath(PathStyle.Windows, "/C:"));
        Assert.Equal("/home/u", PathRules.ToSftpPath(PathStyle.Posix, "/home/u"));
    }

    [Fact]
    public async Task The_existing_ancestor_is_resolved_before_the_boundary_check()
    {
        var guard = new PathGuard(new FakeFs(p => p.Value switch
        {
            "/project/link" => EnvPath.Posix("/outside"),
            "/project/link/new" => throw new EnvironmentException(EnvironmentErrorCode.NotFound),
            _ => p,
        }), PathStyle.Posix);
        var root = EnvPath.Posix("/project");

        var result = await guard.ResolveAsync(root, "link/new");
        Assert.Equal("/outside/new", result.Absolute.Value);
        Assert.True(result.Outside);
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await guard.ResolveWithinAsync(root, "link/new"));
        Assert.Equal(EnvironmentErrorCode.PathEscape, error.Code);
    }

    [Fact]
    public async Task Connection_errors_propagate_instead_of_being_treated_as_a_missing_path()
    {
        var guard = new PathGuard(new FakeFs(_ => throw new EnvironmentException(EnvironmentErrorCode.Disconnected)), PathStyle.Posix);
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await guard.ResolveAsync(EnvPath.Posix("/project"), "src/file"));
        Assert.Equal(EnvironmentErrorCode.Disconnected, error.Code);
    }

    [Fact]
    public async Task Distinct_directories_are_not_folded_on_case_sensitive_volumes()
    {
        var guard = new PathGuard(new FakeFs(p => p.Value == "/Project/link" ? EnvPath.Posix("/project/private") : p), PathStyle.Posix);
        var root = EnvPath.Posix("/Project");
        await Assert.ThrowsAsync<EnvironmentException>(async () => await guard.ResolveWithinAsync(root, "link"));
        Assert.False((await guard.ResolveAsync(root, "src")).Outside);
    }

    [Fact]
    public async Task A_missing_absolute_root_means_outside_but_a_missing_root_with_relative_input_is_an_error()
    {
        var guard = new PathGuard(new FakeFs(p => p.Value.StartsWith("/gone", StringComparison.Ordinal)
            ? throw new EnvironmentException(EnvironmentErrorCode.NotFound) : p), PathStyle.Posix);
        var root = EnvPath.Posix("/gone");
        Assert.True((await guard.ResolveAsync(root, "/elsewhere/file")).Outside);
        await Assert.ThrowsAsync<EnvironmentException>(async () => await guard.ResolveAsync(root, "file"));
    }

    [Fact]
    public async Task Without_a_root_only_absolute_inputs_are_allowed_and_are_always_outside()
    {
        var guard = IdentityGuard(PathStyle.Posix);
        var result = await guard.ResolveAsync(null, "/etc/hosts");
        Assert.True(result.Outside);
        await Assert.ThrowsAsync<EnvironmentException>(async () => await guard.ResolveAsync(null, "relative"));
        await Assert.ThrowsAsync<EnvironmentException>(async () => await guard.ResolveAsync(EnvPath.Posix("/p"), "a\0b"));
    }

    [Theory]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/a/../..", "/")]
    [InlineData("//x///y/./", "/x/y")]
    [InlineData("a/../../b", "../b")]
    [InlineData("", ".")]
    [InlineData("a//b/./c/", "a/b/c")]
    [InlineData(@"a\b", @"a\b")]
    public void Posix_normalize(string input, string expected)
    {
        // POSIX 里反斜杠是普通字符
        Assert.Equal(expected, PathRules.Normalize(PathStyle.Posix, input));
    }

    [Theory]
    [InlineData(@"C:\a\..\..\b", @"C:\b")]
    [InlineData("C:/a/b/", @"C:\a\b")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\srv\share", @"\\srv\share\")]
    [InlineData(@"\\srv\share\x\..\y", @"\\srv\share\y")]
    [InlineData(@"a\..\..\b", @"..\b")]
    public void Windows_normalize(string input, string expected)
    {
        Assert.Equal(expected, PathRules.Normalize(PathStyle.Windows, input));
    }

    [Fact]
    public void Relative_and_inside_follow_the_style_comparison_rules()
    {
        Assert.Equal("../c/d", PathRules.Relative(PathStyle.Posix, "/a/b", "/a/c/d"));
        Assert.Equal("", PathRules.Relative(PathStyle.Posix, "/a", "/a"));
        Assert.Equal("x", PathRules.Relative(PathStyle.Windows, @"C:\Work", @"c:\work\x"));
        Assert.Equal(@"D:\x", PathRules.Relative(PathStyle.Windows, @"C:\Work", @"D:\x"));

        Assert.True(PathRules.IsInside(PathStyle.Posix, "/a/b", "/a/b/c"));
        Assert.True(PathRules.IsInside(PathStyle.Posix, "/a/b", "/a/b"));
        Assert.False(PathRules.IsInside(PathStyle.Posix, "/a/b", "/a/bc"));
        Assert.False(PathRules.IsInside(PathStyle.Posix, "/a/B", "/a/b/c"));
        Assert.True(PathRules.IsInside(PathStyle.Windows, @"C:\Work", @"c:\work\sub"));
        Assert.False(PathRules.IsInside(PathStyle.Windows, @"C:\Work", @"C:\Workshop"));
    }

    [Fact]
    public void Name_helpers()
    {
        Assert.Equal("/a/b", PathRules.Dirname(PathStyle.Posix, "/a/b/c.txt"));
        Assert.Equal("/", PathRules.Dirname(PathStyle.Posix, "/a"));
        Assert.Equal(".", PathRules.Dirname(PathStyle.Posix, "a"));
        Assert.Equal("c.txt", PathRules.Basename(PathStyle.Posix, "/a/b/c.txt"));
        Assert.Equal("", PathRules.Basename(PathStyle.Posix, "/"));
        Assert.Equal(".txt", PathRules.Extname(PathStyle.Posix, "/a/b.c.txt"));
        Assert.Equal("", PathRules.Extname(PathStyle.Posix, "/a/.bashrc"));
        Assert.Equal(@"C:\a", PathRules.Dirname(PathStyle.Windows, @"C:\a\b"));
        Assert.Equal(@"C:\", PathRules.Dirname(PathStyle.Windows, @"C:\a"));
    }

    [Fact]
    public void EnvPath_is_typed_by_style()
    {
        var posix = EnvPath.Posix("/work/src/../lib");
        Assert.Equal("/work/lib", posix.Value);
        Assert.Equal("/work", posix.Parent.Value);
        Assert.Equal("lib", posix.FileName);
        Assert.Equal("/work/lib/a/b.txt", posix.Join("a", "b.txt").Value);
        Assert.Equal("lib/a", posix.Join("a").RelativeTo(EnvPath.Posix("/work")));
        Assert.True(posix.IsWithin(EnvPath.Posix("/work")));

        var windows = EnvPath.Windows(@"C:\work");
        Assert.Throws<EnvironmentException>(() => posix.IsWithin(windows));
        Assert.Throws<EnvironmentException>(() => EnvPath.Absolute("relative", PathStyle.Posix));
        Assert.Throws<EnvironmentException>(() => EnvPath.Parse("a\0b", PathStyle.Posix));
        Assert.Equal(EnvPath.Posix("/a/b"), EnvPath.Posix("/a/./b/"));
    }
}
