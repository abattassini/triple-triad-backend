# Testing SignalR GameHub

This directory contains multiple ways to test the SignalR GameHub functionality.

## Prerequisites

Make sure the backend server is running:
```powershell
dotnet run
```

The server should be running on `http://localhost:5041`

## Testing Methods

### Method 1: Browser-Based Testing (Recommended - Most Visual) 🌐

**File:** `signalr-tester.html`

1. Open `signalr-tester.html` in your browser (double-click or drag to browser)
2. You'll see a nice UI with two player panels
3. Features:
   - ✅ Visual connection status indicators
   - ✅ Separate controls for two players
   - ✅ Real-time event logging
   - ✅ Shared event log to see both players' actions
   - ✅ Test all hub methods: Connect, JoinMatchGroup, PlayCard, SendMessage

**How to use:**
1. Click "Connect" for both players
2. Enter a match ID and click "Join Match" for both
3. Play cards by entering card ID and coordinates, then click "Play Card"
4. Watch events appear in real-time in both player logs and the shared log

### Method 2: Node.js Scripts (Automated Testing) 🤖

**Files:** `08-test-signalr-hub.js`, `09-full-game-flow.js`

#### Setup
```powershell
cd http-requests
npm install
```

#### Test Basic Hub Methods
```powershell
npm run test:hub
# or
node 08-test-signalr-hub.js
```

This tests:
- ✅ Connection to SignalR hub
- ✅ Joining/leaving match groups
- ✅ Sending messages
- ✅ Playing cards
- ✅ Event listeners

#### Test Full Game Flow
```powershell
npm run test:flow
# or
node 09-full-game-flow.js
```

This runs a complete game simulation:
1. ✅ Creates match via REST API
2. ✅ Gets player hands
3. ✅ Connects both players to SignalR
4. ✅ Plays 5 cards alternately
5. ✅ Shows real-time events (cards played, flipped, score updates)
6. ✅ Displays final board state

### Method 3: REST API Files (Setup Only) 📄

The HTTP request files in this directory test the REST API endpoints but NOT SignalR:

- `01-get-cards.http` - Get all cards
- `02-create-match.http` - Create a new match
- `03-get-match.http` - Get match details
- `04-get-player-hand.http` - Get player's hand
- `05-play-card.http` - Play a card (REST endpoint, not SignalR)
- `06-get-waiting-matches.http` - Get waiting matches
- `07-join-match.http` - Join a match
- `00-complete-flow.http` - Complete REST API flow

Use these to set up matches before testing SignalR.

## Typical Testing Workflow

### Quick Visual Test
1. Start backend: `dotnet run`
2. Open `signalr-tester.html` in browser
3. Connect both players and play around

### Automated Full Flow Test
1. Start backend: `dotnet run`
2. Run: `npm run test:flow`
3. Watch the complete game play out in the terminal

### Manual Step-by-Step Test
1. Use REST API files to create a match (`02-create-match.http`)
2. Note the match ID and card IDs from the response
3. Open `signalr-tester.html`
4. Connect both players
5. Enter the match ID and click "Join Match"
6. Use the card IDs from step 2 to play cards
7. Watch the real-time updates!

## SignalR Hub Methods

| Method | Parameters | Description |
|--------|-----------|-------------|
| `JoinMatchGroup` | matchId | Join a match's SignalR group |
| `LeaveMatchGroup` | matchId | Leave a match's SignalR group |
| `PlayCard` | matchId, cardId, x, y, playerId | Play a card (real-time) |
| `SendMessage` | user, message | Send test message |

## SignalR Events (Received from Server)

| Event | Parameters | Description |
|-------|-----------|-------------|
| `CardPlayed` | matchId, cardId, x, y, playerId | A card was played |
| `CardsFlipped` | matchId, cardIds[] | Cards were captured/flipped |
| `ScoreUpdated` | matchId, p1Score, p2Score | Score changed |
| `TurnChanged` | matchId, currentPlayerId | Turn changed to another player |
| `GameEnded` | matchId, winnerId | Game finished |
| `PlayerJoined` | matchId, playerId | Player joined match |
| `ReceiveMessage` | user, message | Test message received |

## Troubleshooting

### "Connection failed"
- Make sure the backend is running on port 5041
- Check CORS settings in `Program.cs`

### "PlayCard failed"
- Make sure you created a match first (via REST API)
- Ensure the card ID exists and belongs to the player
- Check that the board position is valid (0-2 for x and y)
- Verify it's the player's turn

### Node.js scripts not working
- Run `npm install` in the `http-requests` directory
- Make sure you have Node.js installed (v16+)

## Architecture Notes

- **REST API** is used for setup operations (CreateMatch, JoinMatch, GetMatch, etc.)
- **SignalR Hub** is used for real-time gameplay (PlayCard with live updates)
- Both use the same `GamePlayService` for business logic
- SignalR provides instant updates to all connected players in a match group
