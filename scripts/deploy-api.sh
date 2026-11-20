#!/usr/bin/env bash
# Release the API: build an image from a commit and swap it into the EXISTING Cloud Run service.
#
# Only the image changes. Environment variables, secrets, service account, scaling and IAM stay as they are on the
# service. The new revision takes all API traffic at once; the script prints the command that goes back.
#
#   bash scripts/deploy-api.sh                 latest origin/main
#   REF=813fe6c bash scripts/deploy-api.sh     a given commit (or branch, tag)
#   DRY_RUN=1 bash scripts/deploy-api.sh       print what would run, change nothing
#   YES=1 bash scripts/deploy-api.sh           do not ask before deploying
#
# Settings (optional): PROJECT_ID (default: gcloud's project), REGION (asia-east1), SERVICE (vertex-ai-api),
# REPOSITORY (vertex-ai).
set -euo pipefail

# Everything is inside main, so bash has read the whole file before the checkout below can change it on disk.
main() {

  cd "$(dirname "${BASH_SOURCE[0]}")/.."

  PROJECT_ID="${PROJECT_ID:-$(gcloud config get-value project 2>/dev/null || true)}"
  REGION="${REGION:-asia-east1}"
  SERVICE="${SERVICE:-vertex-ai-api}"
  REPOSITORY="${REPOSITORY:-vertex-ai}"
  REF="${REF:-origin/main}"
  DRY_RUN="${DRY_RUN:-0}"
  YES="${YES:-0}"

  die() { echo "错误：$*" >&2; exit 1; }
  run() {
    echo "+ $*"
    if [[ "${DRY_RUN}" != "1" ]]; then "$@"; fi
  }

  [[ -n "${PROJECT_ID}" ]] || die "没有项目。先运行 gcloud config set project <项目 ID>，或设置 PROJECT_ID。"
  [[ -z "$(git status --porcelain --untracked-files=no)" ]] || die "工作区有未提交的改动，先提交或 git stash，免得被切换提交覆盖。"

  echo "== 取代码：${REF}"
  # Only main is fetched: fetching every branch can fail on stale local refs (e.g. "origin/claude" next to
  # "origin/claude/..."), and nothing else is needed for the default. Any other REF must already be known locally.
  git fetch -q origin main || die "拉取 main 失败。可以先运行 git remote prune origin 再试。"
  git checkout -q --detach "${REF}" || die "找不到 ${REF}。如果是别的分支，先运行 git fetch origin <分支名>。"
  COMMIT="$(git rev-parse HEAD)"
  IMAGE="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/vertex-ai-api:${COMMIT}"
  git log --oneline -1

  CURRENT="(DRY_RUN: 未查询)"
  if [[ "${DRY_RUN}" != "1" ]]; then
    CURRENT="$(gcloud run services describe "${SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" \
      --format='value(status.traffic[0].revisionName)')" || die "读不到服务 ${SERVICE}（项目 ${PROJECT_ID}，区域 ${REGION}）。"
    [[ -n "${CURRENT}" ]] || die "服务 ${SERVICE} 没有正在服务的修订。"
  fi

  cat <<INFO

  项目       ${PROJECT_ID}
  区域       ${REGION}
  服务       ${SERVICE}
  当前修订   ${CURRENT}
  新镜像     ${IMAGE}

  部署后，新修订会立即接管全部 API 流量。

INFO

  if [[ "${DRY_RUN}" != "1" && "${YES}" != "1" ]]; then
    read -r -p "继续部署吗？输入 yes 继续：" answer
    [[ "${answer}" == "yes" ]] || die "已取消，什么都没有改。"
  fi

  if [[ "${DRY_RUN}" != "1" ]] && gcloud artifacts docker images describe "${IMAGE}" --project="${PROJECT_ID}" >/dev/null 2>&1; then
    echo "== 镜像已存在，跳过构建"
  else
    echo "== 构建镜像（几分钟）"
    run gcloud builds submit VertexAI --project="${PROJECT_ID}" --region="${REGION}" --tag="${IMAGE}"
  fi

  echo "== 部署"
  run gcloud run deploy "${SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" --image="${IMAGE}"

  NEW="(DRY_RUN)"
  if [[ "${DRY_RUN}" != "1" ]]; then
    NEW="$(gcloud run services describe "${SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" \
      --format='value(status.traffic[0].revisionName)')"
  fi

  cat <<DONE

完成：${SERVICE} 现在由 ${NEW} 服务（提交 ${COMMIT}）。

出问题时，回到部署前的修订：
  gcloud run services update-traffic ${SERVICE} --project=${PROJECT_ID} --region=${REGION} --to-revisions=${CURRENT}=100

DONE
}

main "$@"
exit
