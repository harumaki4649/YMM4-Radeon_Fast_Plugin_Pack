# Radeon AMF プラグイン出力

YMM4(ゆっくりMovieMaker4)での動画書き出しを Radeon GPU 向けに高速化するためのプラグインです。

このフォルダは、リポジトリ全体で必要になっている 3 つの `YukkuriMovieMaker_v4_Lite` 配置先の 1 つです。

- `src/RadeonAmfPlugin/YukkuriMovieMaker_v4_Lite/`
- `src/RadeonAmfPlugin/一時ファイル/`

これらはローカル作業用です。YMM4 本体や一時ファイルを置く前提で使ってください。

YMM4 の `IVideoFileWriter2` が渡す `ID2D1Bitmap1` から D3D11 テクスチャを取得し、CPU にフレームを読み戻さず AMF の DX11 surface としてエンコーダへ渡します。RX 6800 XT では VCN の H.264 / H.265 ハードウェアエンコーダを使います。

## 方針

- NVIDIA NVENC 依存は使用しません
- ffmpeg pipe 経由ではなく AMF SDK を直接使います
- フレームは GPU メモリ上のまま AMF に投入します
- AMF の入力キューを使い、YMM4 のフレーム供給とエンコード出力取得を重ねます
- 音声は Media Foundation AAC、MP4 mux はネイティブ側の writer を使います

## 使い方

1. YMM4の出力形式から「Radeon AMF プラグイン出力」を選択
2. コーデック、品質、ビットレート方式を設定
3. GPUキュー深度を設定
4. 高品質寄りにする場合は「PreAnalysis / 高品質解析を使う」を有効化
5. 出力形式は`.mp4`

各設定項目の詳細については [SETTINGS.md](NVEncVideoWriterPlugin/SETTINGS.md) を参照してください。

## 推奨設定

RX 6800 XT + Ryzen 9 5900X では、まず以下を基準にしてください。

- コーデック: H.265 / HEVC
- 出力品質: 標準または高品質
- ビットレート方式: 自動または可変
- GPUキュー深度: 16
- PreAnalysis: 高品質では有効、速度優先では無効

## 必要環境

- Windows 11
- AMD Radeon GPU
- AMD Software: Adrenalin Edition
- AMF runtime (`amfrt64.dll`) が利用できる AMD ドライバ

## 開発用 SDK

- AMD Advanced Media Framework headers
- Visual Studio C++ Build Tools
- Windows SDK
- YukkuriMovieMaker v4 本体
- C# / .NET の前提は本家 YMM4 の設定に準じます

## ライセンス

- このフォルダの自作コードは MIT License です
- 詳しくは [LICENSE](LICENSE) と [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) を見てください
- YMM4 本体や AMF SDK は別ライセンスです

## デバッグログ

「デバッグログを書き出す」を有効にすると、出力ファイルと同じ場所に `.amf_log.txt` が生成されます。

## 免責事項

本プラグインの利用は自己責任でお願いします。利用によって生じたいかなる損害についても作者は責任を負いません。
