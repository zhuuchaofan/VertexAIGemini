import { expect, test } from "vitest";
import { render, screen } from "@testing-library/react";
import { App } from "./App";

test("renders the skeleton heading", () => {
  render(<App />);
  expect(screen.getByRole("heading", { name: "球球布丁工作室" })).toBeInTheDocument();
});
