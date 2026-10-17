import { useId, useState } from "react";

interface Props {
  label: string;
  value: number | undefined;
  min: number;
  max: number;
  step: number;
  integer?: boolean;
  placeholder: string;
  /** The smallest allowed value is excluded (top-p must be above zero). */
  exclusiveMin?: boolean;
  onChange: (value: number | undefined) => void;
}

/**
 * A number the server will accept or nothing: typing is free, but only a value inside the range is passed on, and a
 * value outside it is explained instead of being quietly clamped.
 */
export function NumberField({ label, value, min, max, step, integer = false, placeholder, exclusiveMin = false, onChange }: Props) {
  const id = useId();
  const [text, setText] = useState(value === undefined ? "" : String(value));
  const [error, setError] = useState<string | null>(null);

  // Follow the value when it is changed from elsewhere (reset, switching models), not while the person is typing.
  const [seen, setSeen] = useState(value);
  if (seen !== value) {
    setSeen(value);
    setText(value === undefined ? "" : String(value));
    setError(null);
  }

  const range = `${exclusiveMin ? "大于 " : ""}${min}${exclusiveMin ? "" : " 至 "}${exclusiveMin ? "，最大 " : ""}${max}`;

  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input
        id={id}
        type="number"
        inputMode="decimal"
        min={min}
        max={max}
        step={step}
        placeholder={placeholder}
        value={text}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${id}-err` : undefined}
        onChange={event => {
          const raw = event.target.value;
          setText(raw);
          if (raw.trim() === "") {
            setError(null);
            onChange(undefined);
            return;
          }

          const number = Number(raw);
          const inRange = Number.isFinite(number) && (exclusiveMin ? number > min : number >= min) && number <= max && (!integer || Number.isInteger(number));
          if (!inRange) {
            setError(integer ? `请输入 ${range} 之间的整数。` : `请输入 ${range} 之间的数。`);
            return;
          }

          setError(null);
          setSeen(number);
          onChange(number);
        }}
        onBlur={() => {
          if (error) {
            setText(value === undefined ? "" : String(value));
            setError(null);
          }
        }}
      />
      {error && <p className="field-error" id={`${id}-err`} role="alert">{error}</p>}
    </div>
  );
}
