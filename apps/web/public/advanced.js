export const safetyLabels = {
  OFF: "关闭可配置过滤（管理员）",
  BLOCK_NONE: "不拦截，保留评分（管理员）",
  BLOCK_ONLY_HIGH: "仅拦截高风险",
  BLOCK_MEDIUM_AND_ABOVE: "拦截中、高风险",
  BLOCK_LOW_AND_ABOVE: "严格：拦截低、中、高风险"
};

export function getAdvancedPayload(values, { supportsSampling = true } = {}) {
  const result = {};
  for (const [key, min, max] of [
    ["temperature", 0, 2], ["topP", Number.MIN_VALUE, 1],
    ["maxOutputTokens", 1, 65536]
  ]) {
    if (!supportsSampling && (key === "temperature" || key === "topP")) continue;
    if (values[key] === "" || values[key] == null) continue;
    const value = Number(values[key]);
    if (!Number.isFinite(value) || value < min || value > max
        || (key === "maxOutputTokens" && !Number.isInteger(value))) {
      throw new Error("高级设置参数超出范围，请检查温度、Top-P 和最大输出长度。");
    }
    result[key] = value;
  }
  if (values.safetyThreshold && Object.hasOwn(safetyLabels, values.safetyThreshold)) {
    result.safetyThreshold = values.safetyThreshold;
  }
  return result;
}
