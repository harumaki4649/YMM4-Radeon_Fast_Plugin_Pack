# プロジェクトロード失敗の可能性原因

`NVEncVideoWriterPlugin.csproj` の先頭で `<TargetFramework>net10.0</TargetFramework>` が指定されていますが、**net10.0** は現時点の .NET SDK では有効なターゲット フレームワークではありません。これにより Visual Studio や dotnet build がプロジェクトを読み込めず、ビルドエラーが発生します。

**修正案**: `net9.0` (または環境で利用可能な最新の `netX.0`) に変更してください。

また、`$(YMM4DirPath)` 環境変数が未定義の場合、プロジェクト内の Reference で参照する DLL のパスが解決できず、読み込みに失敗します。

**修正案**:
- `$(YMM4DirPath)` が設定されていることを確認するか、フォールバックパスを用意してください。

加えて、`<Exec Command="..." Condition="Exists('...'build_native.cmd')"` はスクリプトの存在有無をチェックしていますが、スクリプトの実行自体が失敗するとビルド全体が停止します。

**修正案**:
- `ContinueOnError="true"` を追加して、ネイティブビルドが失敗してもビルドが継続されるようにすると良いでしょう。

最後に、.csproj 内で `$(SkipNativeBuild)` または `$(SkipYmm4Copy)` が明示されていない場合、ビルド後に対象フォルダへのコピーが試みられます。エラーが発生する場合は、これらのプロパティを設定してステップをスキップしてください。
