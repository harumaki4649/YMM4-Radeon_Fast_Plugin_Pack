# Radeon Fast File Source Plugin

YMM4 の画像・音声・動画読み込みを速くするためのプラグインです。

このフォルダは、リポジトリ全体で必要になっている 3 つの `YukkuriMovieMaker_v4_Lite` 配置先の 1 つです。

- `src/RadeonFastFileSourcePlugin/YukkuriMovieMaker_v4_Lite/`
- `src/RadeonFastFileSourcePlugin/一時ファイル/`

上の 2 つはローカル作業用です。配布物には含めず、手元の YMM4 本体や一時ファイルを置く前提で使ってください。

## 何をしているか
- `RadeonFastVideoFileSourcePlugin.cs`
  - 動画ファイルの入口です。
  - FFmpeg と Media Foundation を状況に応じて切り替えます。
  - キャッシュや初回フレームの先読みも担当します。
- `RadeonFastImageFileSourcePlugin.cs`
  - 画像ファイルの入口です。
  - ネイティブデコード、WIC フォールバック、画像キャッシュをまとめます。
- `RadeonFastAudioFileSourcePlugin.cs`
  - 音声ファイルの入口です。
  - MP3 / FFmpeg / Media Foundation を使い分けます。
  - PCM キャッシュもここから使います。
- `WarmupManager.cs`
  - プロジェクト内の素材を記録して、先読みやバックグラウンド処理を動かします。
- `FastFileSourceSettings.cs`
  - 設定の保存・読込を担当します。
- `FastFileSourceLog.cs`
  - ログ出力と初期化処理をまとめます。
- `*Cache.cs` / `Native*`
  - 各種キャッシュとネイティブ読み込み補助です。
- `ProjectWarmupToolPlugin.cs`
  - タイムライン解析や先読みを手動実行するためのツールです。
- `analysis/`
  - YMM4 の内部 API や処理経路を調べるための解析メモです。

## 開発前提
- ビルド対象は `net10.0` です。
- C# の前提は本家 YMM4 の設定に準じます。
- 実際の参照先は `Directory.Build.props` でローカル配置に寄せています。

## ライセンス
- このフォルダの自作コードは MIT License です。
- 詳しくは [LICENSE](LICENSE) と [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) を見てください。
- YMM4 本体、外部 SDK、同梱物は別ライセンスです。
