namespace Hayabumi.Core.Tests;

/// <summary>同じテスト用メールボックスを使うテストを、並行して実行しないためのまとまり。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealServerCollection
{
    public const string Name = "実サーバーを使うテスト";
}
