# 設定ファイルリファレンス (RadeonFastFileSourcePlugin)

設定ファイルの場所: `<YMM4インストール先>/user/RadeonFastFileSourcePlugin/settings.json`

ファイルが存在しない場合はプラグイン初回起動時にデフォルト値で自動生成されます。
設定の変更は **YMM4を再起動しなくても約5秒以内に自動で反映**されます。

> [!CAUTION]
> `Experimental` と付いている項目は動作が不安定になる可能性があります。問題が起きた場合はまず該当項目を `false` に戻してください。

---

## ログ

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableDetailedLog` | bool | `false` | — | すべての読み込みイベントを詳細にログへ出力する。通常は不要。デバッグ時に有効にする。 |

ログファイルの場所: `<YMM4インストール先>/user/log/radeon_fast_filesource_log.txt`

---

## スレッドプール

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableThreadPoolBoost` | bool | `true` | — | .NET のスレッドプール最小スレッド数を強制的に引き上げる。先読みや並列デコードが即座に走るようにするための設定。 |
| `ThreadPoolMinWorkerThreads` | int | `24` | 1〜256 | `EnableThreadPoolBoost` が有効なときに設定するスレッド数の最小値。コア数に応じて増減させてよい。 |

---

## 音声キャッシュ

音声ファイル (MP3 / FFmpeg / Media Foundation) のPCMデータをメモリにキャッシュする機能です。
同じ音声ファイルへの2回目以降のアクセスが瞬時になります。

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableAudioPcmCache` | bool | `true` | — | 音声PCMキャッシュ全体の有効/無効。 |
| `EnableAudioBackgroundPreload` | bool | `false` | — | バックグラウンドで音声ファイルを先読みキャッシュする。有効にするとプロジェクト展開時にCPU使用率が上がる。 |
| `EnableNativeAudioDecoder` | bool | `true` | — | ネイティブDLL (`RadeonFastNativeAudio.dll`) を使った高速オーディオデコードを有効にする。 |
| `EnableNativeTempAudioDecoder` | bool | `false` | — | 一時音声ファイル（YMM4内部生成）にもネイティブデコーダを使う（実験的）。 |
| `AudioCacheMaxMemoryMB` | int | `2048` | 64〜32768 | 音声キャッシュ全体のメモリ上限 (MB)。超えた場合は古いエントリから解放される。 |
| `AudioCacheMaxSingleFileMB` | int | `256` | 8〜8192 | 1ファイルをキャッシュする際の上限 (MB)。これを超えるファイルはキャッシュされない。 |
| `AudioCacheMaxDurationSeconds` | double | `600` | 1〜3600 | キャッシュ対象とする音声の最大再生時間 (秒)。長すぎる音源を除外してメモリを節約する。 |
| `CacheTempAudio` | bool | `false` | — | YMM4が内部で生成する一時音声ファイルをキャッシュする。 |
| `CacheMp3Audio` | bool | `true` | — | MP3ファイルをキャッシュする。 |
| `CacheMediaAudio` | bool | `false` | — | Media Foundation 経由で読んだ音声をキャッシュする。 |
| `AudioCacheMinOpenCount` | int | `2` | 1〜16 | 何回アクセスされたらキャッシュに昇格するかの閾値。1にすると初回からキャッシュされる。 |
| `AudioCacheReadChunkSamples` | int | `65536` | 4096〜1048576 | デコード時の読み込みチャンクサイズ (サンプル数)。大きくするとI/Oが減るがメモリを多く使う。 |
| `AudioCacheMaxConcurrentDecodes` | int | `1` | 1〜32 | 同時に走らせるデコードタスクの最大数。多くするとCPU使用率が上がる。 |

---

## 画像キャッシュ

画像ファイルのデコード済みビットマップをメモリにキャッシュする機能です。

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableImageBitmapCache` | bool | `true` | — | GPU側 (Direct2D `ID2D1Bitmap`) の画像キャッシュを有効にする。 |
| `EnableNativeImageDecoder` | bool | `true` | — | libvips ベースのネイティブ画像デコーダを有効にする。JPEG/PNG/WebP/AVIF/JXL 等で高速。 |
| `EnableImageCpuDecodeCache` | bool | `true` | — | CPUメモリ上にピクセルデータをキャッシュし、GPU転送コストを削減する（Pinned Object Heap使用）。 |
| `ImageCacheMaxMemoryMB` | int | `4096` | 64〜32768 | GPU側ビットマップキャッシュ全体の上限 (MB)。 |
| `ImageCacheMaxSingleFileMB` | int | `256` | 1〜2048 | 1ファイルをGPUビットマップキャッシュする際の上限 (MB)。 |
| `ImageCpuDecodeCacheMaxMemoryMB` | int | `4096` | 64〜32768 | CPUメモリ上のデコード済みキャッシュ全体の上限 (MB)。 |
| `ImageCpuDecodeCacheMaxSingleFileMB` | int | `1024` | 1〜8192 | 1ファイルをCPUキャッシュする際の上限 (MB)。 |

---

## 動画バックエンド選択

動画の読み込みに使うデコーダー（FFmpeg / Media Foundation）の選択ロジックです。

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `PreferMediaFoundationVideo` | bool | `false` | — | `true` にすると常に Media Foundation を優先する。`false` では FFmpeg 優先でフォールバックが MF になる。 |
| `EnableAdaptiveVideoBackend` | bool | `true` | — | ファイルの特性（サイズ・シーク速度・更新速度）を記録し、遅かった場合にバックエンドを自動切り替えする。 |
| `AdaptiveVideoMinFileMB` | int | `16` | 0〜8192 | アダプティブ判定を行う最小ファイルサイズ (MB)。これより小さいファイルは判定対象外。 |
| `AdaptiveVideoLargeJumpMs` | double | `250` | 0〜60000 | シークに何ms以上かかったら「大きなジャンプが遅い」と判断するか (ms)。 |
| `AdaptiveVideoSlowUpdateMs` | double | `20` | 0〜10000 | フレーム更新に何ms以上かかったら「遅い」と判断するか (ms)。 |
| `AdaptiveVideoSlowJumpCount` | int | `8` | 1〜1000 | 「遅いシーク」が何回続いたら切り替えを検討するか。 |
| `AdaptiveVideoPreferenceSeconds` | int | `600` | 1〜86400 | バックエンド切り替えの判断を保持する時間 (秒)。この時間を過ぎると再判定する。 |

---

## 動画ソースキャッシュ

> [!WARNING]
> デフォルトは `false`（無効）です。安定性の検証中のため、有効にする場合は慎重に。

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableVideoSourceCache` | bool | `false` | — | 動画ソースオブジェクトをメモリにキャッシュし、再シーク時の初期化コストをゼロにする。 |
| `VideoSourceCacheMaxEntries` | int | `32` | 0〜128 | キャッシュに保持する動画ソースの最大数。 |
| `VideoSourceCacheMaxProbeEntries` | int | `3` | 0〜32 | キャッシュ候補として試みる動画ソースの最大数。 |
| `VideoSourceCacheTtlSeconds` | int | `180` | 1〜3600 | キャッシュエントリの有効期間 (秒)。期限切れになると解放される。 |
| `VideoSourceCacheMinUpdatesToKeep` | int | `0` | 0〜1000 | キャッシュに残す最低フレーム更新回数。少ししか使われていないソースは退避対象にならない。 |
| `VideoSourceCacheMinSlowUpdateToKeepMs` | double | `20` | 0〜10000 | 更新が何ms以上かかったソースをキャッシュに優先して残すかの閾値 (ms)。 |
| `VideoSourceCachePreferLargeFileMB` | int | `32` | 1〜8192 | このサイズ(MB)以上の動画ファイルを優先してキャッシュに残す。 |
| `VideoSourceCacheMaxFirstSeekJumpSeconds` | double | `2` | 0〜3600 | キャッシュエントリを再利用する際、最初のシークが何秒以内なら再利用するか。 |
| `VideoSourceCacheMinReuseAgeMs` | int | `0` | 0〜30000 | キャッシュエントリを再利用するために最低どのくらい待つか (ms)。 |
| `VideoSourceCacheUseDeviceContextKey` | bool | `true` | — | D3Dデバイスコンテキストもキャッシュキーに含める。異なるデバイスへの誤再利用を防ぐ。 |
| `VideoSourceCacheWaitForWarmupMs` | int | `0` | 0〜1000 | 先読み完了を待つ最大時間 (ms)。0なら待たずにキャッシュを使う。 |

---

## 先読み（Warmup）

プロジェクト内の素材ファイルをバックグラウンドで先読みしておくことで、タイムライン再生開始時のもたつきを軽減します。

### 全体設定

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableProjectWarmup` | bool | `false` | — | プロジェクト全体の先読み機能を有効にするマスタースイッチ。 |
| `EnableAutoProjectWarmup` | bool | `true` | — | YMM4起動後に自動でプロジェクトの先読みを開始する。`EnableProjectWarmup` が false でも音声/画像の個別Warmupは動く。 |
| `EnableTimelineToolWarmup` | bool | `false` | — | タイムラインツールプラグインからの手動先読みトリガーを有効にする。 |
| `WarmupMaxFiles` | int | `96` | 0〜4096 | 1回の先読みで処理するファイルの最大数。 |
| `WarmupMaxConcurrentTasks` | int | `6` | 1〜32 | 先読みを並列実行する最大タスク数。 |
| `WarmupMaxImageFileMB` | int | `512` | 1〜8192 | 先読み対象とする画像ファイルの上限サイズ (MB)。 |
| `WarmupMaxVideoFileMB` | int | `1024` | 1〜16384 | 先読み対象とする動画ファイルの上限サイズ (MB)。 |
| `WarmupReadBufferMB` | int | `4` | 1〜64 | 先読み時のI/O読み込みバッファサイズ (MB)。 |

### 音声の先読み

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableAudioWarmup` | bool | `true` | — | 音声ファイルの先読みを有効にする。 |

### 画像の先読み

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableImageWarmup` | bool | `false` | — | 画像ファイルの先読み（ファイルオープン）を有効にする。 |
| `EnableImageDecodeWarmup` | bool | `true` | — | 画像ファイルのデコード（ピクセル展開まで）を先読みする。 |
| `ImageDecodeWarmupMaxConcurrent` | int | `1` | 1〜4 | 画像デコード先読みの同時実行数。 |

### 動画の先読み

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableVideoFileWarmup` | bool | `false` | — | 動画ファイルのオープン（デコーダ初期化）を先読みする。 |
| `EnableVideoDecodeWarmup` | bool | `false` | — | 動画の最初のフレームデコードまで先読みする。 |
| `VideoDecodeWarmupMaxConcurrent` | int | `1` | 1〜4 | 動画デコード先読みの同時実行数。 |
| `VideoDecodeWarmupFrames` | int | `2` | 1〜8 | 先読みで何フレームデコードするか。 |
| `EnableVideoInitialFrameWarmup` | bool | `false` | — | 動画ソース生成時に最初の1フレームを即座にデコードして初期化時間を前倒しする。 |
| `VideoDecodeWarmupMaxQueuedPerCall` | int | `8` | 1〜128 | 1回の先読みキュー呼び出しで積む最大動画数。 |

---

## 内部APIプローブ・インジェクションプロファイラー

> [!CAUTION]
> これらはボトルネック調査用の開発者向け機能です。通常の使用では有効にする必要はありません。YMM4の動作が不安定になる場合があります。

### 内部APIプローブ

| キー | 型 | デフォルト | 説明 |
| :--- | :--- | :--- | :--- |
| `EnableInternalApiProbe` | bool | `false` | YMM4の内部APIをリフレクションで解析するプローブを有効にする。 |

### インジェクションプロファイラー（0Harmony）

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableInjectionProfiler` | bool | `false` | — | 0Harmonyを使ってYMM4内部メソッドにフックし、処理時間を計測する。 |
| `EnableInjectionArgumentLog` | bool | `false` | — | フックした呼び出しの引数内容をログに記録する。 |
| `InjectionSlowThresholdMs` | double | `8` | 0〜10000 | この時間 (ms) を超えた処理を「遅い」と判定してログに記録するか。 |
| `InjectionRenderSlowThresholdMs` | double | `25` | 0〜10000 | レンダリング系処理の「遅い」判定閾値 (ms)。 |
| `InjectionSummaryInterval` | int | `1000` | 0〜100000 | 処理統計をログに書き出す間隔（フレーム数）。 |

### スロー引数ログ

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableSlowArgumentPropertyLog` | bool | `false` | — | 処理が遅かったときに引数のプロパティ内容もログに残す。 |
| `SlowArgumentPropertyLogThresholdMs` | double | `35` | 0〜10000 | 引数ログを記録する処理時間の閾値 (ms)。 |
| `SlowArgumentPropertyMaxPerType` | int | `6` | 0〜1000 | 型ごとに記録する引数ログの最大件数。 |
| `EnableSlowArgumentPathWarmup` | bool | `false` | — | 遅い処理で現れたファイルパスをWarmup対象に追加する。 |

### レンダーシーンパスWarmup

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableRenderScenePathWarmup` | bool | `false` | — | レンダリングシーンから素材パスを事前に収集してWarmupする。 |
| `RenderScenePathWarmupMaxPaths` | int | `512` | 0〜8192 | 収集するパスの最大数。 |
| `RenderScenePathWarmupMaxDepth` | int | `8` | 1〜16 | シーングラフの探索深さの上限。 |
| `RenderScenePathWarmupMaxCollectionItems` | int | `2048` | 1〜65536 | コレクション型プロパティから展開するアイテムの最大数。 |

---

## PSD関連キャッシュ

`RadeonFastFileSourcePlugin` は PSD 立ち絵の高速化機能も内包しています。

### PSD状態キャッシュ

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnablePsdStateCache` | bool | `true` | — | 立ち絵の状態（口・目・差分）が前フレームと同一の場合に再描画をスキップする。 |
| `PsdStateCacheLogInterval` | int | `500` | 0〜100000 | スキップ統計をログに書き出す間隔（フレーム数）。0で無効。 |

### PSDマニフェスト（差分予測先読み）

次に表示されそうな立ち絵の状態をあらかじめデコードしておく先読み機能です。

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnablePsdStateManifest` | bool | `true` | — | 立ち絵の出現パターンを記録したマニフェストを使った先読みを有効にする。 |
| `EnablePsdManifestFileWarmup` | bool | `true` | — | マニフェストから抽出したファイルを先読みする。 |
| `PsdManifestMaxStates` | int | `512` | 16〜8192 | マニフェストに記録する最大状態数。 |
| `PsdManifestCandidateLogCount` | int | `12` | 0〜256 | 先読み候補としてログに記録する件数。 |
| `PsdManifestCandidateMinPrepareMs` | double | `20` | 0〜10000 | 先読み候補とみなす最低準備時間 (ms)。これ以上かかった状態を優先的に先読みする。 |

### PSD内部APIプローブ

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnablePsdInternalApiProbe` | bool | `false` | — | PSD処理の内部APIをリフレクションで解析するプローブを有効にする。開発者向け。 |
| `PsdInternalApiProbeMaxTypes` | int | `40` | 0〜512 | プローブで解析する最大型数。 |

### PSDフィールドキャッシュ（実験的）

> [!WARNING]
> 以下はすべて `Experimental` 機能です。不安定になる場合があります。

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableExperimentalPsdFieldCache` | bool | `false` | — | PSDのレイヤーフィールドをキャッシュして、差分だけを再計算する実験的機能。 |
| `PsdFieldCacheReplaceMinPrepareMs` | double | `50` | 0〜10000 | 何ms以上かかった場合にフィールドキャッシュへの置き換えを試みるか (ms)。 |

### PSD並列プリロード（実験的）

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableExperimentalPsdParallelPreload` | bool | `false` | — | 立ち絵ファイルのプリロードを複数スレッドで並列実行する。 |
| `PsdParallelPreloadMaxEntries` | int | `32` | 0〜512 | 並列プリロードするエントリの最大数。 |
| `PsdParallelPreloadMaxConcurrent` | int | `2` | 1〜16 | 同時実行するプリロードスレッド数。 |
| `PsdParallelPreloadWaitMs` | int | `0` | 0〜1000 | プリロード完了を待つ最大時間 (ms)。0なら待たずに進む。 |

### PSDレイヤー先行デコード（実験的）

| キー | 型 | デフォルト | 有効範囲 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `EnableExperimentalPsdLayerPredecode` | bool | `false` | — | PSDのレイヤーを事前にデコードしておく実験的機能。 |
| `PsdLayerPredecodeMaxLayers` | int | `128` | 0〜1024 | 先行デコードするレイヤーの最大数。 |

---

## 並列インジェクション（実験的）

| キー | 型 | デフォルト | 説明 |
| :--- | :--- | :--- | :--- |
| `EnableExperimentalParallelInjection` | bool | `false` | プロファイラーインジェクションを並列処理する実験的モード。 |

---

## 設定例

### 標準的な用途（メモリ16GB以上）

```json
{
  "EnableThreadPoolBoost": true,
  "ThreadPoolMinWorkerThreads": 24,
  "EnableAudioPcmCache": true,
  "CacheMp3Audio": true,
  "AudioCacheMaxMemoryMB": 2048,
  "EnableNativeImageDecoder": true,
  "EnableImageCpuDecodeCache": true,
  "ImageCacheMaxMemoryMB": 4096,
  "ImageCpuDecodeCacheMaxMemoryMB": 4096,
  "EnableAdaptiveVideoBackend": true,
  "EnableAudioWarmup": true,
  "EnableImageDecodeWarmup": true,
  "EnablePsdStateCache": true,
  "EnablePsdStateManifest": true
}
```

### メモリを節約したい場合

```json
{
  "AudioCacheMaxMemoryMB": 512,
  "AudioCacheMaxSingleFileMB": 64,
  "ImageCacheMaxMemoryMB": 1024,
  "ImageCacheMaxSingleFileMB": 64,
  "ImageCpuDecodeCacheMaxMemoryMB": 512,
  "ImageCpuDecodeCacheMaxSingleFileMB": 128,
  "WarmupMaxFiles": 32
}
```

### トラブル発生時のデバッグ設定

```json
{
  "EnableDetailedLog": true,
  "EnableInjectionProfiler": true,
  "InjectionSlowThresholdMs": 10,
  "EnableSlowArgumentPropertyLog": true,
  "SlowArgumentPropertyLogThresholdMs": 20
}
```
