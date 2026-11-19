import { expect, test } from "@playwright/test";
import type { Browser, Page, WebSocketRoute } from "@playwright/test";

/** A player in their own browser context, with a switch that cuts their connection like a network failure. */
async function player(browser: Browser, name: string): Promise<{ page: Page; goOffline: () => Promise<void>; goOnline: () => void }> {
  const context = await browser.newContext();
  await context.addInitScript((value) => localStorage.setItem("chess.name", value), name);
  const page = await context.newPage();

  let offline = false;
  const open = new Set<WebSocketRoute>();
  await page.routeWebSocket(/\/mqtt$/, (socket) => {
    if (offline) {
      void socket.close();
      return;
    }

    const server = socket.connectToServer();
    open.add(socket);
    socket.onClose(() => {
      open.delete(socket);
      void server.close();
    });
  });

  return {
    page,
    goOffline: async () => {
      offline = true;
      await Promise.all([...open].map((socket) => socket.close()));
    },
    goOnline: () => {
      offline = false;
    },
  };
}

async function move(page: Page, from: string, to: string): Promise<void> {
  await page.locator(`[data-square="${from}"]`).click();
  await page.locator(`[data-square="${to}"]`).click();
}

test("two players play a game, one drops and comes back, and it ends in checkmate", async ({ browser }) => {
  const alice = await player(browser, "alice");
  const bob = await player(browser, "bob");

  // Alice creates a game and gets a link without the "create" part.
  await alice.page.goto("/");
  await alice.page.getByRole("button", { name: "Create a game" }).click();
  await expect(alice.page.locator("#status")).toHaveText("Waiting for an opponent. Send them the invite link.");
  await expect(alice.page).toHaveURL(/#\/g\/[0-9a-f]{32}$/);
  await expect(alice.page.locator("#role")).toHaveText("You play white");

  // Bob opens the link and takes black. Alice's page shows it without a reload.
  await bob.page.goto(alice.page.url());
  await expect(bob.page.locator("#role")).toHaveText("You play black");
  await expect(alice.page.locator("#status")).toHaveText("Your move.");
  await expect(bob.page.locator("#status")).toHaveText("alice to move.");
  await expect(alice.page.locator('[data-seat="black"] .who')).toHaveText("bob");
  await expect(alice.page.locator('[data-seat="black"] .presence')).toHaveText("online");

  // Black sits at the bottom of bob's board.
  await expect(bob.page.locator("#board [data-square]").first()).toHaveAttribute("data-square", "h1");
  await expect(alice.page.locator("#board [data-square]").first()).toHaveAttribute("data-square", "a8");

  // 1. f3 e5
  await move(alice.page, "f2", "f3");
  await expect(bob.page.locator("#status")).toHaveText("Your move.");
  await expect(bob.page.locator('[data-square="f3"] .piece')).toHaveCount(1);
  await move(bob.page, "e7", "e5");
  await expect(alice.page.locator("#status")).toHaveText("Your move.");

  // The server refuses an illegal move, and nothing changes.
  await move(alice.page, "g2", "g6");
  await expect(alice.page.locator("#notice")).toHaveText("That move is not legal");
  await expect(alice.page.locator('[data-square="g2"] .piece')).toHaveCount(1);

  // 2. g4
  await move(alice.page, "g2", "g4");
  await expect(bob.page.locator("#status")).toHaveText("Your move.");

  // Bob's connection drops. Alice sees "reconnecting"; bob sees that he is offline and cannot move.
  await bob.goOffline();
  await expect(alice.page.locator('[data-seat="black"] .presence')).toHaveText("reconnecting…");
  await expect(bob.page.locator("#banner")).toHaveText("Connection lost. Reconnecting…");

  // He comes back and gets the whole game again, with nobody having to reload.
  bob.goOnline();
  await expect(bob.page.locator("#banner")).toHaveCount(0);
  await expect(alice.page.locator('[data-seat="black"] .presence')).toHaveText("online");
  await expect(bob.page.locator("#status")).toHaveText("Your move.");
  await expect(bob.page.locator("#moves li")).toHaveCount(2);

  // 2... Qh4#
  await move(bob.page, "d8", "h4");
  for (const page of [alice.page, bob.page]) {
    await expect(page.locator("#status")).toHaveText("Black wins (checkmate).");
    await expect(page.locator("#moves")).toContainText("Qh4#");
    await expect(page.locator("#resign")).toHaveCount(0);
  }
});

test("a third visitor watches, and a link to a game that does not exist says so", async ({ browser }) => {
  const alice = await player(browser, "alice");
  const bob = await player(browser, "bob");
  const carol = await player(browser, "carol");

  await alice.page.goto("/");
  await alice.page.getByRole("button", { name: "Create a game" }).click();
  await expect(alice.page).toHaveURL(/#\/g\/[0-9a-f]{32}$/);
  await bob.page.goto(alice.page.url());
  await expect(bob.page.locator("#role")).toHaveText("You play black");
  await move(alice.page, "e2", "e4");

  await carol.page.goto(alice.page.url());
  await expect(carol.page.locator("#role")).toHaveText("You are watching");
  await expect(carol.page.locator('[data-square="e4"] .piece')).toHaveCount(1);
  await move(bob.page, "c7", "c5");
  await expect(carol.page.locator("#moves li")).toHaveCount(1);
  await expect(carol.page.locator("#moves")).toContainText("c5");

  // Resigning ends the game for everyone.
  await bob.page.getByRole("button", { name: "Resign" }).click();
  await expect(carol.page.locator("#status")).toHaveText("White wins (resignation).");

  await carol.page.goto("/#/g/00000000000000000000000000000000");
  await expect(carol.page.locator(".status")).toHaveText("This game does not exist.");
});

async function drag(page: Page, from: string, to: string): Promise<void> {
  const start = (await page.locator(`[data-square="${from}"]`).boundingBox())!;
  const end = (await page.locator(`[data-square="${to}"]`).boundingBox())!;
  await page.mouse.move(start.x + start.width / 2, start.y + start.height / 2);
  await page.mouse.down();
  await page.mouse.move(end.x + end.width / 2, end.y + end.height / 2, { steps: 8 });
  await page.mouse.up();
}

test("a piece can be dragged to a square, and a drop that is not a move puts it back", async ({ browser }) => {
  const alice = await player(browser, "alice");
  const bob = await player(browser, "bob");

  await alice.page.goto("/");
  await alice.page.getByRole("button", { name: "Create a game" }).click();
  await expect(alice.page).toHaveURL(/#\/g\/[0-9a-f]{32}$/);
  await bob.page.goto(alice.page.url());
  await expect(alice.page.locator("#status")).toHaveText("Your move.");
  await expect(alice.page.locator("#how-to-move")).toContainText("drag it to a square");

  // 1. e4, dragged.
  await drag(alice.page, "e2", "e4");
  await expect(bob.page.locator("#status")).toHaveText("Your move.");
  await expect(bob.page.locator('[data-square="e4"] .piece')).toHaveCount(1);

  // Black's board is turned round, and dragging works the same way. 1... e5
  await drag(bob.page, "e7", "e5");
  await expect(alice.page.locator("#status")).toHaveText("Your move.");
  await expect(alice.page.locator('[data-square="e5"] .piece')).toHaveCount(1);

  // Dropping a piece on one of your own puts it back without asking the server.
  await drag(alice.page, "d1", "d2");
  await expect(alice.page.locator("#notice")).toHaveCount(0);
  await expect(alice.page.locator("#status")).toHaveText("Your move.");

  // A drop on a square the piece cannot reach is refused by the server, and nothing changes.
  await drag(alice.page, "a1", "a5");
  await expect(alice.page.locator("#notice")).toHaveText("That move is not legal");
  await expect(alice.page.locator('[data-square="a1"] .piece')).toHaveCount(1);
  await expect(alice.page.locator(".ghost")).toHaveCount(0);

  // A click after all that still selects a piece, so the two ways of moving live side by side.
  await move(alice.page, "g1", "f3");
  await expect(bob.page.locator("#status")).toHaveText("Your move.");
  await expect(bob.page.locator("#moves")).toContainText("Nf3");
});
