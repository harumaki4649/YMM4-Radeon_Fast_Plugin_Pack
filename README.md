# YMM4-Radeon_Fast_Plugin_Pack
YMM4(ゆっくりMovieMaker4)向けの Radeon 最適化プラグイン群です。

このリポジトリは OSS として公開する前提で、コード本体と配布用の注意事項をまとめています。

## プラグイン一覧
- `src/RadeonAmfPlugin/`
  - AMF を直接使う動画書き出しプラグインです。
  - GPU メモリ上のフレームをそのままエンコーダへ渡し、Radeon GPU のハードウェアエンコードを使います。
- `src/RadeonFastFileSourcePlugin/`
  - 画像・音声・動画の読み込みを高速化するプラグインです。
  - キャッシュ、先読み、バックエンド切り替え、ログ記録をまとめています。
- `src/RadeonFastPsdTachiePlugin/`
  - PSD / 立ち絵まわりの読み込みや先読みを扱うプラグインです。
  - YMM4 の内部挙動を調べるための補助コードも含みます。

## 必要な配置
次の 3 つのフォルダでは、ローカルに `YukkuriMovieMaker_v4_Lite` を置いてビルドする前提です。

- `src/RadeonAmfPlugin/YukkuriMovieMaker_v4_Lite/`
- `src/RadeonFastFileSourcePlugin/YukkuriMovieMaker_v4_Lite/`
- `src/RadeonFastPsdTachiePlugin/YukkuriMovieMaker_v4_Lite/`

あわせて、各フォルダの `一時ファイル/` もローカル作業用の置き場として使います。
これらは配布物ではなく、手元の YMM4 本体や作業用ファイルを置く場所です。

## 開発前提
- ビルド対象は `net10.0` です。
- C# の言語機能やビルド前提は、基本的に本家 YMM4 の設定に準じます。
- このリポジトリでは `Directory.Build.props` で YMM4 の参照先をローカル配置に寄せています。

## ソースの役割
- `src/RadeonAmfPlugin/NVEncVideoWriterPlugin/`
  - YMM4 から渡されたフレームを Radeon AMF に流す本体です。
- `src/RadeonFastFileSourcePlugin/NVEncVideoWriterPlugin/RadeonFastFileSource/`
  - 音声、画像、動画の高速読み込みとキャッシュの本体です。
- `src/RadeonFastPsdTachiePlugin/RadeonFastPsdTachiePlugin/`
  - PSD 立ち絵の状態判定と更新ログの本体です。
- `src/*/AmfNative/`
  - AMF 用のネイティブ補助ライブラリです。
- `src/RadeonFastFileSourcePlugin/analysis/`
  - YMM4 の内部 API やフレーム処理を調べるための解析コードと出力です。
- `src/*/tools/AssemblyDump/`
  - アセンブリの中身を確認するための補助ツールです。
- `src/*/vendor/`
  - 外部依存の同梱物です。変更時はライセンス確認が必要です。

## ライセンス
- このリポジトリの自作コードは MIT License です。
- 各フォルダの `LICENSE` に同じ MIT ライセンスが入っています。
- `THIRD_PARTY_NOTICES.txt` には、YMM4 や外部 SDK など、別途扱いが必要な依存物をまとめています。
- YMM4 本体やその配布物は、このリポジトリには含めません。

## 開発協力
協力してもらえると助かるもの:

- 再現手順つきの不具合報告
- 実機環境での速度差や安定性のフィードバック
- README の誤解しやすい箇所の修正
- 小さな修正 PR

報告してもらえると嬉しい情報:

- YMM4 の版
- OS と GPU
- どのフォルダのプラグインか
- 使った設定
- `user/log/` のログ

まずは各フォルダの README を見て、必要な配置を整えてから触るのがいちばん安全です。
