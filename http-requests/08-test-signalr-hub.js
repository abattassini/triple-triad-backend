// Test SignalR GameHub
// Run this with: node 08-test-signalr-hub.js
// First install: npm install @microsoft/signalr

const signalR = require("@microsoft/signalr");

const baseUrl = "http://localhost:5041";
const testPlayer1 = "test-player-1";
const testPlayer2 = "test-player-2";

async function testGameHub() {
    console.log("🎮 Testing SignalR GameHub...\n");

    // Create SignalR connection
    const connection = new signalR.HubConnectionBuilder()
        .withUrl(`${baseUrl}/gamehub`)
        .withAutomaticReconnect()
        .configureLogging(signalR.LogLevel.Information)
        .build();

    // Set up event listeners
    connection.on("ReceiveMessage", (user, message) => {
        console.log(`📨 [ReceiveMessage] ${user}: ${message}`);
    });

    connection.on("CardPlayed", (matchId, cardId, x, y, playerId) => {
        console.log(`🎴 [CardPlayed] Match ${matchId}: Player ${playerId} played card ${cardId} at (${x}, ${y})`);
    });

    connection.on("CardsFlipped", (matchId, cardIds) => {
        console.log(`🔄 [CardsFlipped] Match ${matchId}: Cards flipped:`, cardIds);
    });

    connection.on("ScoreUpdated", (matchId, player1Score, player2Score) => {
        console.log(`📊 [ScoreUpdated] Match ${matchId}: P1=${player1Score}, P2=${player2Score}`);
    });

    connection.on("GameEnded", (matchId, winnerId) => {
        console.log(`🏆 [GameEnded] Match ${matchId}: Winner is ${winnerId}`);
    });

    connection.on("PlayerJoined", (matchId, playerId) => {
        console.log(`👤 [PlayerJoined] Match ${matchId}: Player ${playerId} joined`);
    });

    connection.on("TurnChanged", (matchId, currentPlayerId) => {
        console.log(`🔄 [TurnChanged] Match ${matchId}: Current player is ${currentPlayerId}`);
    });

    try {
        // Start connection
        console.log("🔌 Connecting to SignalR hub...");
        await connection.start();
        console.log("✅ Connected successfully!\n");        // Test 1: Join a match group
        console.log("📍 Test 1: Joining match group...");
        await connection.invoke("JoinMatch", 1);
        console.log("✅ Joined match group 1\n");

        // Test 2: Send a test message
        console.log("💬 Test 2: Sending test message...");
        await connection.invoke("SendMessage", testPlayer1, "Hello from SignalR!");
        await sleep(1000);

        // Test 3: Play a card (requires existing match and card)
        // Note: You need to create a match first via REST API
        console.log("🎴 Test 3: Playing a card...");
        try {
            await connection.invoke("PlayCard", 1, 1, 0, 0, testPlayer1);
            console.log("✅ Card played successfully\n");
        } catch (error) {
            console.log(`⚠️ PlayCard failed (expected if no match exists): ${error.message}\n`);
        }        // Test 4: Leave match group
        console.log("🚪 Test 4: Leaving match group...");
        await connection.invoke("LeaveMatch", 1);
        console.log("✅ Left match group 1\n");

        // Wait a bit to see any final messages
        await sleep(2000);

    } catch (error) {
        console.error("❌ Error:", error.message);
    } finally {
        console.log("\n🔌 Closing connection...");
        await connection.stop();
        console.log("✅ Connection closed");
    }
}

function sleep(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

// Run the test
testGameHub().catch(console.error);
