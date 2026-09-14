using Stakeout.Core;

namespace Stakeout.Core.Tests;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stakeout-config-").FullName;

    private string Write(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void 設定ファイルが無ければ既定値になる()
    {
        var loaded = ConfigLoader.LoadFrom(new[] { Path.Combine(_dir, "missing.json") });

        Assert.Empty(loaded.LoadedPaths);
        Assert.Equal("envdte", loaded.Config.Backend);
        Assert.Equal(20, loaded.Config.Limits.WaitSec);

        // allowProcesses は既定で空 = 全拒否（design.md §15.3）
        Assert.Empty(loaded.Config.AllowProcesses);
    }

    [Fact]
    public void 後から読んだ設定が前の設定に勝つ()
    {
        var user = Write("user.json", """{ "backend": "envdte", "allowLaunch": false }""");
        var project = Write("project.json", """{ "backend": "dbgeng" }""");

        var loaded = ConfigLoader.LoadFrom(new[] { user, project });

        Assert.Equal("dbgeng", loaded.Config.Backend);

        // 上書きしていない項目は下の層のものが残る
        Assert.False(loaded.Config.AllowLaunch);
        Assert.Equal(new[] { user, project }, loaded.LoadedPaths);
    }

    [Fact]
    public void 入れ子は深くマージされ指定しなかった兄弟は既定値のまま残る()
    {
        // limits.waitSec だけ変えたつもりで limits ごと既定に戻る、という事故を防ぐ
        var user = Write("user.json", """{ "limits": { "waitSec": 45, "maxHits": 7 } }""");
        var project = Write("project.json", """{ "limits": { "waitSec": 5 } }""");

        var loaded = ConfigLoader.LoadFrom(new[] { user, project });

        Assert.Equal(5, loaded.Config.Limits.WaitSec);
        Assert.Equal(7, loaded.Config.Limits.MaxHits);
        Assert.Equal(60, loaded.Config.Limits.CompositeSec);
    }

    [Fact]
    public void 配列は要素単位ではなく丸ごと置き換わる()
    {
        var user = Write("user.json", """{ "allowProcesses": ["^A\\.exe$", "^B\\.exe$"] }""");
        var project = Write("project.json", """{ "allowProcesses": ["^C\\.exe$"] }""");

        var loaded = ConfigLoader.LoadFrom(new[] { user, project });

        Assert.Equal(new[] { "^C\\.exe$" }, loaded.Config.AllowProcesses);
    }

    [Fact]
    public void コメントと末尾カンマを許す()
    {
        var path = Write("project.json", """
            {
              // 既定の backend
              "backend": "dbgeng",
            }
            """);

        var loaded = ConfigLoader.LoadFrom(new[] { path });

        Assert.Equal("dbgeng", loaded.Config.Backend);
    }

    [Fact]
    public void 壊れた設定は既定値に落ちずに例外になる()
    {
        var path = Write("broken.json", "{ this is not json");

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadFrom(new[] { path }));

        Assert.Equal(path, ex.Path);
    }

    [Fact]
    public void トップレベルがオブジェクトでなければ例外になる()
    {
        var path = Write("array.json", "[1, 2, 3]");

        Assert.Throws<ConfigException>(() => ConfigLoader.LoadFrom(new[] { path }));
    }

    [Fact]
    public void 探索順はユーザー設定が先でプロジェクト設定が後になる()
    {
        var paths = ConfigLoader.DefaultSearchPaths(@"C:\work\proj");

        Assert.Equal(2, paths.Count);
        Assert.EndsWith(Path.Combine("stakeout", ConfigLoader.UserFileName), paths[0]);
        Assert.Equal(Path.Combine(@"C:\work\proj", ConfigLoader.ProjectFileName), paths[1]);
    }

    [Fact]
    public void プロジェクト設定は親ディレクトリへ遡って探す()
    {
        // エージェントが cd した先でデーモンが自動起動しても、設定を読めるようにする（ADR 0023）
        var project = Write(ConfigLoader.ProjectFileName, "{}");
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "target", "NativeLib")).FullName;

        var paths = ConfigLoader.DefaultSearchPaths(sub);

        Assert.Equal(project, paths[1]);
    }

    [Fact]
    public void 遡るときは最も近いプロジェクト設定を読む()
    {
        Write(ConfigLoader.ProjectFileName, "{}");
        var nearer = Directory.CreateDirectory(Path.Combine(_dir, "a")).FullName;
        File.WriteAllText(Path.Combine(nearer, ConfigLoader.ProjectFileName), "{}");
        var sub = Directory.CreateDirectory(Path.Combine(nearer, "b")).FullName;

        Assert.Equal(Path.Combine(nearer, ConfigLoader.ProjectFileName), ConfigLoader.FindProjectFile(sub));
    }

    [Fact]
    public void 相対の_gtagsRoot_は設定ファイルの場所を基準に解決する()
    {
        // 作業ディレクトリ基準で解くと、親から見つけた設定では別の場所を指す
        var path = Write(ConfigLoader.ProjectFileName, """{ "code": { "gtagsRoot": "samples/target" } }""");

        var loaded = ConfigLoader.LoadFrom(new[] { path });

        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "samples", "target")), loaded.Config.Code.GtagsRoot);
    }

    [Fact]
    public void 絶対パスの_gtagsRoot_はそのまま使う()
    {
        var absolute = Path.Combine(_dir, "elsewhere");
        var path = Write(
            ConfigLoader.ProjectFileName,
            $$"""{ "code": { "gtagsRoot": "{{absolute.Replace("\\", "\\\\")}}" } }""");

        var loaded = ConfigLoader.LoadFrom(new[] { path });

        Assert.Equal(absolute, loaded.Config.Code.GtagsRoot);
    }

    [Fact]
    public void allowUnelevatedClients_はプロジェクト設定に書くと止まる()
    {
        // クローンしただけで管理者デバッガへの経路が開いてはいけない（ADR 0024）
        var path = Write(ConfigLoader.ProjectFileName, """{ "pipe": { "allowUnelevatedClients": true } }""");

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadFrom(new[] { path }));

        Assert.Contains("allowUnelevatedClients", ex.Message);
    }

    [Fact]
    public void allowUnelevatedClients_はユーザー設定なら効く()
    {
        var user = Write(ConfigLoader.UserFileName, """{ "pipe": { "allowUnelevatedClients": true } }""");
        var project = Write(ConfigLoader.ProjectFileName, """{ "backend": "envdte" }""");

        var loaded = ConfigLoader.LoadFrom(new[] { user, project });

        Assert.True(loaded.Config.Pipe.AllowUnelevatedClients);
    }

    [Fact]
    public void allowUnelevatedClients_は既定で無効()
    {
        Assert.False(ConfigLoader.LoadFrom(Array.Empty<string>()).Config.Pipe.AllowUnelevatedClients);
    }

    [Fact]
    public void envdte_の_progId_は既定で未指定になる()
    {
        // ADR 0002: 版数を固定せず ROT から自動検出する
        var loaded = ConfigLoader.LoadFrom(Array.Empty<string>());

        Assert.Null(loaded.Config.EnvDte.ProgId);
        Assert.Null(loaded.Config.EnvDte.VsPid);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
