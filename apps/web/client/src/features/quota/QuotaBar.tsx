import type { QuotaStatus } from "../../api/types";
import { describeReset, summarize } from "./quota";

/** A thin bar for how much of today's allowance is used, with the details a tap away. */
export function QuotaBar({ status }: { status: QuotaStatus | null }) {
  const summary = summarize(status);
  if (!summary) return null;
  return (
    <details className="quota" data-level={summary.level}>
      <summary>
        <span className="quota-label">
          今日额度{summary.level === "full" ? "已用完" : summary.level === "warn" ? "快用完了" : ""}
        </span>
        <span className="quota-track" role="progressbar" aria-label="今日额度" aria-valuemin={0} aria-valuemax={100} aria-valuenow={summary.percent}>
          <span className="quota-fill" style={{ width: `${summary.percent}%` }} />
        </span>
        <span className="quota-percent">{summary.percent}%</span>
      </summary>
      <dl className="quota-lines">
        {summary.lines.map(line => (
          <div key={line.key} data-fullest={line.key === summary.fullest.key || undefined}>
            <dt>{line.label}</dt>
            <dd>
              {line.used} / {line.limit}
            </dd>
          </div>
        ))}
      </dl>
      <p className="quota-reset">{describeReset(summary.resetsAt)} 重置</p>
    </details>
  );
}
