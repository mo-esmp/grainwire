// Drawing the board from a FEN string. The browser knows nothing about the rules of chess:
// the server decides what is legal, and every "moved" event carries the new position.

export type Color = "white" | "black";

export interface Piece {
  color: Color;
  kind: "k" | "q" | "r" | "b" | "n" | "p";
}

const glyphs: Record<Piece["kind"], string> = { k: "♚", q: "♛", r: "♜", b: "♝", n: "♞", p: "♟" };
const names: Record<Piece["kind"], string> = { k: "king", q: "queen", r: "rook", b: "bishop", n: "knight", p: "pawn" };
const files = ["a", "b", "c", "d", "e", "f", "g", "h"] as const;

/** Reads the piece placement of a FEN into a map from square name ("e2") to piece. */
export function parseFen(fen: string): Map<string, Piece> {
  const position = new Map<string, Piece>();
  const rows = (fen.split(" ")[0] ?? "").split("/");
  rows.forEach((row, index) => {
    const rank = 8 - index;
    let file = 0;
    for (const char of row) {
      if (char >= "1" && char <= "8") {
        file += Number(char);
        continue;
      }

      const kind = char.toLowerCase() as Piece["kind"];
      position.set(`${files[file]}${rank}`, { color: char === kind ? "black" : "white", kind });
      file++;
    }
  });
  return position;
}

export interface BoardOptions {
  /** Which side sits at the bottom. */
  orientation: Color;
  selected?: string;
  lastMove?: [string, string];
  /** Squares the viewer may click. */
  interactive: boolean;
}

/** Renders the 64 squares as buttons. Clicks are handled by the caller through event delegation on the container. */
export function renderBoard(container: HTMLElement, position: Map<string, Piece>, options: BoardOptions): void {
  const ranks = options.orientation === "white" ? [8, 7, 6, 5, 4, 3, 2, 1] : [1, 2, 3, 4, 5, 6, 7, 8];
  const columns = options.orientation === "white" ? [...files] : [...files].reverse();
  const squares: string[] = [];
  for (const rank of ranks) {
    for (const file of columns) {
      const square = `${file}${rank}`;
      const piece = position.get(square);
      const dark = (files.indexOf(file) + rank) % 2 === 1;
      const classes = ["square", dark ? "dark" : "light"];
      if (options.selected === square) classes.push("selected");
      if (options.lastMove?.includes(square)) classes.push("last");
      const label = piece ? `${square}, ${piece.color} ${names[piece.kind]}` : `${square}, empty`;
      const content = piece ? `<span class="piece ${piece.color}" aria-hidden="true">${glyphs[piece.kind]}</span>` : "";
      const corner =
        (file === columns[0] ? `<span class="coord rank">${rank}</span>` : "") +
        (rank === ranks[7] ? `<span class="coord file">${file}</span>` : "");
      squares.push(
        `<button type="button" class="${classes.join(" ")}" data-square="${square}" aria-label="${label}"${options.interactive ? "" : ' tabindex="-1"'}>${content}${corner}</button>`,
      );
    }
  }

  container.innerHTML = squares.join("");
  container.classList.toggle("interactive", options.interactive);
}
