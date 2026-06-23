# Radeon Fast PSD Tachie Plugin

YMM4 の PSD 立ち絵まわりを軽くするためのプラグインです。

このフォルダは、リポジトリ全体で必要になっている 3 つの `YukkuriMovieMaker_v4_Lite` 配置先の 1 つです。

- `src/RadeonFastPsdTachiePlugin/YukkuriMovieMaker_v4_Lite/`
- `src/RadeonFastPsdTachiePlugin/一時ファイル/`

これらはローカル作業用です。YMM4 本体や一時ファイルを置く前提で使ってください。

## 何をしているか
- `RadeonFastPsdTachiePlugin.cs`
  - `PsdTachiePlugin` を包み、同じ状態の更新をまとめてスキップします。
  - 更新時間を測って、重い更新をログに残します。
- `TachieStateKey`
  - 立ち絵の状態を文字列化して、同じ更新かどうかを判定します。
- `FastPsdTachieLog`
  - `user/log/radeon_fast_psd_tachie_log.txt` にログを書きます。

## 開発前提
- ビルド対象は `net10.0` です。
- C# の前提は本家 YMM4 の設定に準じます。
- 実際の参照先は `Directory.Build.props` でローカル配置に寄せています。

## ライセンス
- このフォルダの自作コードは MIT License です。
- 詳しくは [LICENSE](LICENSE) と [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) を見てください。
- YMM4 本体や外部 SDK は別ライセンスです。
