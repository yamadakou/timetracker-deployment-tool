#!/usr/bin/env bash
# システムテスト（本物の Azure に対するテスト）をローカルで実行する。手順は docs/SYSTEM_TESTS.md を参照。
#
# 使い方:
#   scripts/run-system-tests.sh <サブスクリプション ID> [権限の少ないサービスプリンシパルのクライアント ID]
#
# Azure CLI にサインインしていなければデバイスコード認証でサインインする。権限不足のシナリオは、
# クライアント ID とシークレット（環境変数 TT_SYSTEMTEST_LIMITED_CLIENT_SECRET か入力）がある場合だけ実行する。
set -euo pipefail

subscription="${1:?サブスクリプション ID を指定してください}"
limited_client_id="${2:-${TT_SYSTEMTEST_LIMITED_CLIENT_ID:-}}"
repo="$(cd "$(dirname "$0")/.." && pwd)"

if ! az account show --output none 2>/dev/null; then
  az login --use-device-code --output none
fi
az account set --subscription "$subscription"

export TT_SYSTEMTEST_SUBSCRIPTION_ID="$subscription"
export TT_SYSTEMTEST_LOCATION="${TT_SYSTEMTEST_LOCATION:-japaneast}"
export TT_SYSTEMTEST_LIMITED_RESOURCE_GROUP="${TT_SYSTEMTEST_LIMITED_RESOURCE_GROUP:-rg-ttst-limited}"
if [ -n "$limited_client_id" ]; then
  export TT_SYSTEMTEST_LIMITED_CLIENT_ID="$limited_client_id"
  TT_SYSTEMTEST_LIMITED_TENANT_ID="$(az account show --query tenantId --output tsv)"
  export TT_SYSTEMTEST_LIMITED_TENANT_ID
  if [ -z "${TT_SYSTEMTEST_LIMITED_CLIENT_SECRET:-}" ]; then
    read -r -s -p "権限の少ないサービスプリンシパルのシークレット: " TT_SYSTEMTEST_LIMITED_CLIENT_SECRET
    echo
    export TT_SYSTEMTEST_LIMITED_CLIENT_SECRET
  fi
else
  echo "警告: クライアント ID が無いため、権限不足のシナリオはスキップします。" >&2
fi

# global.json（Microsoft.Testing.Platform を使う設定）を読むため、リポジトリのディレクトリで実行する
cd "$repo"
# セットアップウィザードとログインの確認は、ライセンスキー（環境変数 TT_E2E_LICENSE_KEY）とサンプルデータベースのユーザー（TT_E2E_SAMPLE_LOGIN / TT_E2E_SAMPLE_PASSWORD）がある場合だけ行う。使うブラウザーを入れる（PowerShell が必要）
if [ -n "${TT_E2E_LICENSE_KEY:-}" ]; then
  dotnet build "$repo/tests/TTDeployTool.SystemTests"
  pwsh "$repo/tests/TTDeployTool.SystemTests/bin/Debug/net10.0/playwright.ps1" install chromium
else
  echo "警告: TT_E2E_LICENSE_KEY が無いため、セットアップウィザードとログインの確認はスキップします。" >&2
fi

echo "システムテストを実行します（20〜40 分かかり、Azure Container Apps の料金が発生します）。"
# --results-directory は "=" でつなぐ（既存のディレクトリをスペース区切りで渡すと、dotnet test がプロジェクトの指定と誤解する）
dotnet test --project "$repo/tests/TTDeployTool.SystemTests" -- \
  --output detailed --report-xunit-trx "--results-directory=$repo/TestResults/system"
