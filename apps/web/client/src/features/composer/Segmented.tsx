interface Props<T extends string> {
  label: string;
  value: T | undefined;
  options: { value: T; label: string }[];
  onChange: (value: T) => void;
}

/** A radio group drawn as a segmented control: arrow keys move between the choices. */
export function Segmented<T extends string>({ label, value, options, onChange }: Props<T>) {
  return (
    <div className="segmented" role="radiogroup" aria-label={label}>
      {options.map((option, index) => {
        const selected = option.value === value;
        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={selected}
            tabIndex={selected || (value === undefined && index === 0) ? 0 : -1}
            className={selected ? "seg on" : "seg"}
            onClick={() => onChange(option.value)}
            onKeyDown={event => {
              const step = event.key === "ArrowRight" || event.key === "ArrowDown" ? 1 : event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 0;
              if (!step) return;
              event.preventDefault();
              const next = options[(index + step + options.length) % options.length];
              if (next) {
                onChange(next.value);
                (event.currentTarget.parentElement?.children[(index + step + options.length) % options.length] as HTMLElement | undefined)?.focus();
              }
            }}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}
