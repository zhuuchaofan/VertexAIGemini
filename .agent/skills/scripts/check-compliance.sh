#!/bin/bash
# Skills 合规性检查脚本
# 用法: ./check-compliance.sh [目录路径]

set -e

TARGET_DIR="${1:-./VertexAI}"
ERRORS=0

echo "=========================================="
echo "🔍 Skills 合规性检查"
echo "=========================================="
echo ""

# 1. 检查文件行数
echo "📏 检查文件行数限制..."
echo "-------------------------------------------"

check_file_lines() {
    local file="$1"
    local max_lines="$2"
    local lines=$(wc -l < "$file" | tr -d ' ')

    if [ "$lines" -gt "$max_lines" ]; then
        echo "❌ $file: $lines 行 (限制 $max_lines)"
        ERRORS=$((ERRORS + 1))
    fi
}

# 检查 .cs 服务类 (最大 300 行)
for file in $(find "$TARGET_DIR" -name "*.cs" -path "*/Services/*" 2>/dev/null); do
    check_file_lines "$file" 300
done

# 检查 .razor 页面 (最大 400 行)
for file in $(find "$TARGET_DIR" -name "*.razor" -path "*/Pages/*" 2>/dev/null); do
    check_file_lines "$file" 400
done

# 检查 .razor 组件 (最大 200 行)
for file in $(find "$TARGET_DIR" -name "*.razor" ! -path "*/Pages/*" 2>/dev/null); do
    check_file_lines "$file" 200
done

echo ""

# 2. 检查硬编码密钥模式
echo "🔐 检查硬编码密钥..."
echo "-------------------------------------------"

SENSITIVE_PATTERNS=(
    "AIza[0-9A-Za-z_-]{35}"      # Google API Key
    "sk-[a-zA-Z0-9]{32,}"        # OpenAI API Key
    "ghp_[a-zA-Z0-9]{36}"        # GitHub Personal Token
    "xoxb-[a-zA-Z0-9-]+"         # Slack Bot Token
    "password\s*=\s*\"[^\"]+\""  # 硬编码密码
)

for pattern in "${SENSITIVE_PATTERNS[@]}"; do
    matches=$(grep -rEn "$pattern" "$TARGET_DIR" --include="*.cs" --include="*.json" 2>/dev/null | grep -v "appsettings.Production.json" || true)
    if [ -n "$matches" ]; then
        echo "❌ 发现潜在硬编码密钥:"
        echo "$matches"
        ERRORS=$((ERRORS + 1))
    fi
done

echo ""

# 3. 检查空 catch 块
echo "⚠️  检查空 catch 块..."
echo "-------------------------------------------"

empty_catches=$(grep -rn "catch\s*{" "$TARGET_DIR" --include="*.cs" 2>/dev/null || true)
if [ -n "$empty_catches" ]; then
    echo "❌ 发现空 catch 块:"
    echo "$empty_catches"
    ERRORS=$((ERRORS + 1))
fi

echo ""

# 4. 检查 .gitignore 安全配置
echo "📁 检查 .gitignore 配置..."
echo "-------------------------------------------"

GITIGNORE_FILE="$TARGET_DIR/../.gitignore"
REQUIRED_IGNORES=(".env" "*.key" "appsettings.*.json")

if [ -f "$GITIGNORE_FILE" ]; then
    for item in "${REQUIRED_IGNORES[@]}"; do
        if ! grep -q "$item" "$GITIGNORE_FILE" 2>/dev/null; then
            echo "⚠️  .gitignore 缺少: $item"
        fi
    done
else
    echo "⚠️  未找到 .gitignore 文件"
fi

echo ""
echo "=========================================="
if [ "$ERRORS" -gt 0 ]; then
    echo "❌ 发现 $ERRORS 个问题"
    exit 1
else
    echo "✅ 所有检查通过"
    exit 0
fi
