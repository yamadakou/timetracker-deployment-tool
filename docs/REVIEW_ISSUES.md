# コードレビュー指摘事項と対応状況

- 優先度: P1（最優先）〜 P4（低）。この順に対応する。
- 重大度: 高 / 中 / 低
- コミットハッシュ: 対応を行ったコミット。コミット自身のハッシュはそのコミット内に書けないため、次のコミットで記入する。

| No. | 重大度 | 優先度 | 問題の分類 | 問題の内容 | 対応方針 | 対応状況 | コミットハッシュ |
|---|---|---|---|---|---|---|---|
| 1 | 高 | P1 | 不具合 | Dry-run で生成される `docker-compose.yml` の `environment:` が `KEY=value` を `- ` 無しで並べており、PostgreSQL 版は文字列扱い、SQL Server 版は YAML 構文エラーになる。`docker compose up` が失敗する。テストも誤った形式を正としていた。 | `environment:` をマップ形式（`KEY: "value"`）で出力する。YamlDotNet でパースして environment がマップであることを検証するテストを追加する。併せて廃止済みの `version:` 行を削除。 | 対応済（修正前は新テスト4件失敗→修正後全件成功。生成ファイルを `docker compose config` で検証し両 DB 種別とも OK） | |
| 2 | 高 | P1 | 不具合 | アプリ名の上限チェックが 32 文字だが、実際の Container App 名は `{appName}-redis` 等の接尾辞付き。27 文字以上だと Redis 作成時に Azure 側で失敗し、中途半端なリソースが残る。 | 最長接尾辞（`-redis`）を考慮し、アプリ名の上限を 26 文字にする。エラーメッセージ・README も更新。 | 未着手 | |
| 3 | 高 | P1 | 不具合 | 既存リソースグループに `CreateOrUpdate(location のみ)` を実行するためタグが消える／リージョン不一致でエラーになる。Container Apps Environment も PUT で既存設定を上書きする。 | 存在確認を行い、既存ならそのまま使う（RG はリージョン不一致時に警告）。存在しない場合のみ作成する。 | 未着手 | |
| 4 | 高 | P2 | 運用・データ | DB が永続ボリューム無しで稼働。再実行（リビジョン更新）で DB コンテナが再起動しデータが消えるが、README は再実行での設定変更を案内している。 | Azure Files 永続化は影響範囲が大きいため本対応では見送り、README に明確な注意を追記し、既存 DB アプリ更新時に警告ログを出す。 | 未着手 | |
| 5 | 高 | P1 | セキュリティ | DB パスワード・アプリパスワードを Container Apps の通常環境変数（平文 Value）で渡しており、ポータル/ARM 上で閲覧できる。 | `Configuration.Secrets` に登録し、環境変数は `SecretRef` で参照する。コンテナ定義生成を純粋関数化してテスト追加。 | 未着手 | |
| 6 | 中 | P2 | セキュリティ | `sp-env` 指定時に環境変数が不足すると `DefaultAzureCredential` に黙ってフォールバックし、意図しない資格情報でデプロイされ得る。 | フォールバックせず例外で停止する。テストを更新。 | 未着手 | |
| 7 | 中 | P3 | セキュリティ・ライセンス | SQL Server: 非推奨の `SA_PASSWORD` を使用、compose で 1433 をホスト公開、`Developer` エディションは本番利用不可。`Defaults` に固定パスワードが残存。 | `MSSQL_SA_PASSWORD` に変更、compose のホスト公開を削除、README にライセンス注意を追記、未使用の固定パスワード定数を削除。 | 未着手 | |
| 8 | 中 | P2 | 不具合 | `double.TryParse` / `$"{x}Gi"` がカルチャ依存。小数点がカンマのロケールで `0.75`→75 vCPU、`1,5Gi` になる。解析失敗時も黙って既定値。 | `CultureInfo.InvariantCulture` で解析・整形し、解析失敗はエラーにする。テスト追加。 | 未着手 | |
| 9 | 中 | P2 | 不具合・UX | 引数解析: `--dry-run false` でも dry-run 有効、未知オプションを無視、`--help` 無し、未使用の System.CommandLine 参照。 | 引数解析を `CliArgs` クラスに切り出し、bool フラグの true/false 値対応・未知オプションのエラー化・`--help` を追加。未使用パッケージ参照を削除。テスト追加。 | 未着手 | |
| 10 | 中 | P2 | UX | `--db-type sqlserver` のみ指定すると既定タグ `7.0-linux-postgres` と不一致でエラーになる。 | `--tt-tag` 未指定時は DB 種別に応じた既定タグ（`7.0-linux-mssql`）を使う。テスト追加。 | 未着手 | |
| 11 | 中 | P3 | 不整合 | compose（`DB_HOST` 等の旧変数のみ、`postgres:16-alpine`/`redis:7-alpine`）と Azure（`TTNX_*`、`postgres:16`/`redis:latest`）で環境変数・イメージが不一致。`redis:latest` は未固定。 | 動作確認済みの Azure 側を正とし、compose に `TTNX_*` 変数を追加。イメージを `postgres:16` / `redis:7` に統一し、定数化して共有する。 | 未着手 | |
| 12 | 低 | P3 | 不整合・検証 | `Defaults.DbName = "timetracker_db"` が実際の既定値 `timetracker` と不一致。SQL Server のパスワード複雑性要件を検証していない。 | 既定値を `Defaults` に集約し整合させる。sqlserver 時は複雑性要件（4種中3種）を検証。テスト追加。 | 未着手 | |
| 13 | 低 | P3 | テスト | リポジトリ直下の不要な `ComposeGeneratorTests.cs`、何も検証しない `RunTests` テスト、`Validate` のテスト不足。 | 不要ファイル・無意味なテストを削除し、`Validate` のテストを追加。 | 未着手 | |
| 14 | 低 | P3 | CI | `release.yml` が RID/単一ファイル指定なしで publish（.NET ランタイム必須の配布物になり README と不一致）、リリース前にテスト未実行。 | README 記載と同じ `-r win-x64` 単一ファイル self-contained で publish し、事前にテストを実行する。 | 未着手 | |
| 15 | 低 | P4 | 保守性 | `.sln` で一部プロジェクトがソリューションフォルダ未所属。エラーが stdout に `ex.Message` のみで出力され `--verbose` でもスタックトレースが出ない。 | ソリューションフォルダを整理。エラーを stderr に出力し、`--verbose` 時はスタックトレースも出す。 | 未着手 | |
