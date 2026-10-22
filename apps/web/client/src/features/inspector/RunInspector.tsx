import type { RunSnapshot } from "../../api/types";
import { NoteExtras } from "../chat/NoteExtras";
import {
  browserTimingRows, outputRows, overviewRows, searchRows, serverTimingRows, settingRows, usageRows, type Observed, type Row
} from "./runDetails";

interface Props {
  run: RunSnapshot | null;
  observed?: Observed;
  /** The run this one retried, or the one that retried it, to jump between attempts. */
  onOpenRun?: (runId: string) => void;
}

function Rows({ rows }: { rows: Row[] }) {
  return (
    <dl className="kv">
      {rows.map(row => (
        <div key={row.label} className="kv-row">
          <dt>{row.label}</dt>
          <dd className={row.mono ? "mono" : undefined}>{row.value}</dd>
        </div>
      ))}
    </dl>
  );
}

export function RunInspector({ run, observed, onOpenRun }: Props) {
  if (!run) {
    return (
      <div className="run-inspector empty-run" tabIndex={0} role="region" aria-label="运行详情内容">
        <p>在回复下方点“运行详情”，这里会显示那一次生成实际发生的事。</p>
      </div>
    );
  }

  const settings = settingRows(run);
  const usage = usageRows(run);
  const browser = browserTimingRows(observed);
  const search = searchRows(run);

  return (
    <div className="run-inspector" data-run-id={run.runId} tabIndex={0} role="region" aria-label="运行详情内容">
      <section aria-labelledby="ri-overview">
        <h3 id="ri-overview">概览</h3>
        <Rows rows={overviewRows(run)} />
        {run.error && (
          <div className="note err" role="status">
            <strong>{run.error.message}</strong> <span className="mono">{run.error.code}</span>
            {run.error.retryable ? " · 可重试" : ""}
            <NoteExtras
              note={{
                hint: run.error.hint ?? undefined,
                admin: run.error.detail || run.error.adminHint ? { detail: run.error.detail ?? undefined, hint: run.error.adminHint ?? undefined } : undefined
              }}
            />
          </div>
        )}
        {(run.retryOfRunId || run.retriedByRunId) && (
          <p className="run-links">
            {run.retryOfRunId && (
              <button type="button" className="link" onClick={() => onOpenRun?.(run.retryOfRunId!)}>
                查看被重试的一次
              </button>
            )}
            {run.retriedByRunId && (
              <button type="button" className="link" onClick={() => onOpenRun?.(run.retriedByRunId!)}>
                查看重试后的一次
              </button>
            )}
          </p>
        )}
      </section>

      <section aria-labelledby="ri-settings">
        <h3 id="ri-settings">设置：请求与实际</h3>
        <table className="cmp">
          <thead>
            <tr>
              <th scope="col">项</th>
              <th scope="col">请求</th>
              <th scope="col">实际</th>
            </tr>
          </thead>
          <tbody>
            {settings.map(row => (
              <tr key={row.label} data-differs={row.differs || undefined}>
                <th scope="row">{row.label}</th>
                <td>{row.requested}</td>
                <td>
                  {row.effective}
                  {row.differs && <span className="tag warn"> 不同</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="hint">“默认”表示没有指定；“不适用”表示该模型不接受这一项。</p>
      </section>

      {search && (
        <section aria-labelledby="ri-search">
          <h3 id="ri-search">联网检索</h3>
          <Rows rows={search} />
        </section>
      )}

      <section aria-labelledby="ri-server">
        <h3 id="ri-server">服务端时间</h3>
        <Rows rows={serverTimingRows(run)} />
      </section>

      <section aria-labelledby="ri-browser">
        <h3 id="ri-browser">本窗口观察</h3>
        {browser ? <Rows rows={browser} /> : <p className="hint">只有在本窗口发出的生成才有；刷新或在别处打开后没有这项。</p>}
      </section>

      <section aria-labelledby="ri-usage">
        <h3 id="ri-usage">用量（{usage.source}）</h3>
        <Rows rows={usage.rows} />
      </section>

      <section aria-labelledby="ri-output">
        <h3 id="ri-output">输出与调用</h3>
        <Rows rows={outputRows(run)} />
      </section>
    </div>
  );
}
