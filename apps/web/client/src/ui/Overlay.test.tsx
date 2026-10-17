import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { Overlay } from "./Overlay";

function Harness() {
  const [open, setOpen] = useState(false);
  return (
    <div>
      <button type="button">outside</button>
      <Overlay
        open={open}
        onClose={() => setOpen(false)}
        title="选择"
        trigger={<button type="button" data-overlay-trigger onClick={() => setOpen(true)}>open</button>}
      >
        <button type="button">inside</button>
      </Overlay>
    </div>
  );
}

describe("Overlay", () => {
  it("opens, closes on Escape and returns focus to the control that opened it", () => {
    render(<Harness />);
    const trigger = screen.getByRole("button", { name: "open" });
    fireEvent.click(trigger);
    expect(screen.getByRole("dialog", { name: "选择" })).toBeInTheDocument();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(trigger).toHaveFocus();
  });

  it("closes on a press outside but not on a press inside", () => {
    render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: "open" }));
    fireEvent.pointerDown(screen.getByRole("button", { name: "inside" }));
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    fireEvent.pointerDown(screen.getByRole("button", { name: "outside" }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
