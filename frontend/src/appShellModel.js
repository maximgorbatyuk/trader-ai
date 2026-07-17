export async function loadShellSnapshot(client) {
  const [marketResponse, player] = await Promise.all([client.getMarket(), client.getPlayer()])
  const market = marketResponse ?? (await client.seedMarket())
  return { market, player }
}
