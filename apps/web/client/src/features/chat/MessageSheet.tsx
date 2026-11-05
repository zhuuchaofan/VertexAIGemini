import { useEffect, useRef, useState } from "react";
import { copyText } from "../../ui/clipboard";
import { speakableText } from "./messageText";

interface Props {
  text: string;
  onClose: () => void;
  onQuote?: (text: string) => void;
  onInspect?: () => void;
}

/** What can be done with one message, as a bottom sheet on a phone and a small card on a wide screen. */
export function MessageSheet({ text, onClose, onQuote, onInspect }: Props) {
  const panel = useRef<HTMLDivElement>(null);
  const [mode, setMode] = useState<"menu" | "select">("menu");
  const [copied, setCopied] = useState<"idle" | "ok" | "failed">("idle");
  const [speaking, setSpeaking] = useState(false);
  // The finger that opened this sheet is still on the screen. Its lift sends a click to whatever is under it now,
  // so clicks are ignored for a moment.
  const armed = useRef(false);
  const canSpeak = typeof window !== "undefined" && "speechSynthesis" in window;

  useEffect(() => {
    const arm = setTimeout(() => {
      armed.current = true;
    }, 400);
    panel.current?.focus();
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => {
      clearTimeout(arm);
      document.removeEventListener("keydown", onKey);
      if (canSpeak) window.speechSynthesis.cancel();
    };
  }, [onClose, canSpeak]);

  const toggleSpeech = () => {
    if (!canSpeak) return;
    if (speaking) {
      window.speechSynthesis.cancel();
      setSpeaking(false);
      return;
    }
    const utterance = new SpeechSynthesisUtterance(speakableText(text));
    utterance.lang = "zh-CN";
    utterance.onend = () => setSpeaking(false);
    utterance.onerror = () => setSpeaking(false);
    window.speechSynthesis.cancel();
    window.speechSynthesis.speak(utterance);
    setSpeaking(true);
  };

  return (
    <>
      <div className="msg-sheet-backdrop" onClick={() => armed.current && onClose()} />
      <div ref={panel} className="msg-sheet" role="dialog" aria-label="消息操作" tabIndex={-1} onClickCapture={event => !armed.current && event.stopPropagation()}>
        <span className="sheet-grip" aria-hidden="true" />
        {mode === "select" ? (
          <>
            <p className="hint">按住文字拖动，选取要复制的部分。</p>
            <div className="select-text">{text}</div>
            <button type="button" className="msg-sheet-item" onClick={() => setMode("menu")}>返回</button>
          </>
        ) : (
          <>
            <button
              type="button"
              className="msg-sheet-item"
              onClick={async () => {
                setCopied((await copyText(text)) ? "ok" : "failed");
                setTimeout(onClose, 600);
              }}
            >
              <span aria-hidden="true">📋</span>
              <span aria-live="polite">{copied === "ok" ? "已复制" : copied === "failed" ? "复制失败" : "复制整条"}</span>
            </button>
            {onQuote && (
              <button
                type="button"
                className="msg-sheet-item"
                onClick={() => {
                  onQuote(text);
                  onClose();
                }}
              >
                <span aria-hidden="true">💬</span>
                <span>引用回复</span>
              </button>
            )}
            <button type="button" className="msg-sheet-item" onClick={() => setMode("select")}>
              <span aria-hidden="true">✂️</span>
              <span>选择文字</span>
            </button>
            {canSpeak && (
              <button type="button" className="msg-sheet-item" onClick={toggleSpeech}>
                <span aria-hidden="true">{speaking ? "⏹" : "🔊"}</span>
                <span>{speaking ? "停止朗读" : "朗读"}</span>
              </button>
            )}
            {onInspect && (
              <button
                type="button"
                className="msg-sheet-item"
                onClick={() => {
                  onInspect();
                  onClose();
                }}
              >
                <span aria-hidden="true">🔍</span>
                <span>运行详情</span>
              </button>
            )}
          </>
        )}
      </div>
    </>
  );
}
