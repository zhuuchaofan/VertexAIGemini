import { useEffect, useRef, useState } from "react";

interface Props {
  initial: string;
  error: string | null;
  onSave: (title: string) => Promise<void>;
  onCancel: () => void;
  className?: string;
}

/** A title edited in place: Enter saves, Escape or leaving the field without a change puts it back. */
export function RenameField({ initial, error, onSave, onCancel, className }: Props) {
  const [value, setValue] = useState(initial);
  const [saving, setSaving] = useState(false);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    input.current?.focus();
    input.current?.select();
  }, []);

  const commit = async () => {
    const title = value.trim();
    if (!title || title === initial.trim()) {
      onCancel();
      return;
    }

    setSaving(true);
    try {
      await onSave(title);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className={`rename ${className ?? ""}`}>
      <input
        ref={input}
        aria-label="会话名称"
        value={value}
        maxLength={80}
        disabled={saving}
        aria-invalid={error ? true : undefined}
        onChange={event => setValue(event.target.value)}
        onKeyDown={event => {
          if (event.key === "Enter" && !event.nativeEvent.isComposing) {
            event.preventDefault();
            void commit();
          }
          if (event.key === "Escape") {
            event.stopPropagation();
            onCancel();
          }
        }}
        onBlur={() => {
          if (!saving) void commit();
        }}
      />
      {error && <p className="field-error" role="alert">{error}</p>}
    </div>
  );
}
