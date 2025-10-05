// Full game flow test using both REST API and SignalR
// Run this with: node 09-full-game-flow.js
// First install: npm install @microsoft/signalr axios

const signalR = require("@microsoft/signalr");
const axios = require("axios");

const baseUrl = "http://localhost:5041";
const apiUrl = `${baseUrl}/api/game`;
const hubUrl = `${baseUrl}/gamehub`;

const player1Id = "test-player-1";
const player2Id = "test-player-2";

let matchId;
let player1Cards = [];
let player2Cards = [];
let cardNamesMap = {}; // Map card IDs to names

async function createConnection(playerId) {
    const connection = new signalR.HubConnectionBuilder()
        .withUrl(hubUrl)
        .withAutomaticReconnect()
        .configureLogging(signalR.LogLevel.Warning)
        .build();    // Set up event listeners
    connection.on("CardPlayed", (data) => {
        const cardName = cardNamesMap[data.cardId] || `Card ${data.cardId}`;
        console.log(`  🎴 [${playerId}] ${cardName} played at (${data.x}, ${data.y}) by ${data.playerId}`);
        if (data.capturedCards && data.capturedCards.length > 0) {
            console.log(`  🔄 [${playerId}] Cards captured:`, data.capturedCards.map(c => `(${c.x}, ${c.y})`).join(", "));
        }
        console.log(`  📊 [${playerId}] Score: P1=${data.player1Score}, P2=${data.player2Score}`);
    });

    connection.on("GameCompleted", (data) => {
        console.log(`  🏆 [${playerId}] Game ended! Winner: ${data.winnerId || 'Draw'}`);
    });

    connection.on("MatchJoined", (data) => {
        console.log(`  👤 [${playerId}] Joined match ${data.matchId}`);
    });

    await connection.start();
    return connection;
}

async function testFullGameFlow() {
    console.log("🎮 Full Game Flow Test with SignalR\n");
    console.log("=" .repeat(60) + "\n");

    let connection1, connection2;

    try {
        // Step 1: Create match via REST API
        console.log("📍 Step 1: Creating match via REST API...");
        const createResponse = await axios.post(`${apiUrl}/match`, {
            playerId: player1Id,
            opponentId: player2Id
        });
          matchId = createResponse.data.match.id;
        player1Cards = createResponse.data.playerHand;
        
        // Build card names map for both players
        player1Cards.forEach(card => {
            cardNamesMap[card.id] = card.name;
        });
        
        console.log(`✅ Match created: ID ${matchId}`);
        console.log(`   Player 1 cards:`, player1Cards.map(c => `${c.name} (ID: ${c.id})`).join(", "));
        console.log();

        // Step 2: Get Player 2's hand
        console.log("📍 Step 2: Getting Player 2's hand...");
        const handResponse = await axios.get(`${apiUrl}/match/${matchId}/hand/${player2Id}`);
        player2Cards = handResponse.data.filter(c => !c.isUsed);
        
        // Add player 2's cards to the map
        player2Cards.forEach(card => {
            cardNamesMap[card.id] = card.name;
        });
        
        console.log(`✅ Player 2 cards:`, player2Cards.map(c => `${c.name} (ID: ${c.id})`).join(", "));
        console.log();

        // Step 3: Connect both players to SignalR
        console.log("📍 Step 3: Connecting players to SignalR...");
        connection1 = await createConnection("Player1");
        connection2 = await createConnection("Player2");
        console.log("✅ Both players connected\n");        // Step 4: Join match groups
        console.log("📍 Step 4: Joining match groups...");
        await connection1.invoke("JoinMatch", matchId);
        await connection2.invoke("JoinMatch", matchId);
        console.log("✅ Both players joined match group\n");

        await sleep(1000);        // Step 5: Play cards alternately via SignalR - FULL GAME (9 cards)
        console.log("📍 Step 5: Playing COMPLETE game via SignalR (9 cards)...");
        console.log("=" .repeat(60));        // Player 1 plays card at (0,0)
        console.log(`\n🎯 Player 1's turn - Playing ${player1Cards[0].name} at (0,0)`);
        await connection1.invoke("PlayCard", matchId, player1Cards[0].id, 0, 0, player1Id);
        await sleep(1000);

        // Player 2 plays card at (1,0)
        console.log(`\n🎯 Player 2's turn - Playing ${player2Cards[0].name} at (1,0)`);
        await connection2.invoke("PlayCard", matchId, player2Cards[0].id, 1, 0, player2Id);
        await sleep(1000);

        // Player 1 plays card at (0,1)
        console.log(`\n🎯 Player 1's turn - Playing ${player1Cards[1].name} at (0,1)`);
        await connection1.invoke("PlayCard", matchId, player1Cards[1].id, 0, 1, player1Id);
        await sleep(1000);

        // Player 2 plays card at (2,0)
        console.log(`\n🎯 Player 2's turn - Playing ${player2Cards[1].name} at (2,0)`);
        await connection2.invoke("PlayCard", matchId, player2Cards[1].id, 2, 0, player2Id);
        await sleep(1000);

        // Player 1 plays card at (1,1)
        console.log(`\n🎯 Player 1's turn - Playing ${player1Cards[2].name} at (1,1)`);
        await connection1.invoke("PlayCard", matchId, player1Cards[2].id, 1, 1, player1Id);
        await sleep(1000);

        // Player 2 plays card at (2,1)
        console.log(`\n🎯 Player 2's turn - Playing ${player2Cards[2].name} at (2,1)`);
        await connection2.invoke("PlayCard", matchId, player2Cards[2].id, 2, 1, player2Id);
        await sleep(1000);

        // Player 1 plays card at (0,2)
        console.log(`\n🎯 Player 1's turn - Playing ${player1Cards[3].name} at (0,2)`);
        await connection1.invoke("PlayCard", matchId, player1Cards[3].id, 0, 2, player1Id);
        await sleep(1000);

        // Player 2 plays card at (1,2)
        console.log(`\n🎯 Player 2's turn - Playing ${player2Cards[3].name} at (1,2)`);
        await connection2.invoke("PlayCard", matchId, player2Cards[3].id, 1, 2, player2Id);
        await sleep(1000);

        // Player 1 plays card at (2,2) - FINAL CARD!
        console.log(`\n🎯 Player 1's turn - Playing ${player1Cards[4].name} at (2,2) - FINAL CARD!`);
        await connection1.invoke("PlayCard", matchId, player1Cards[4].id, 2, 2, player1Id);
        await sleep(1500);

        console.log("\n" + "=" .repeat(60));
        console.log("🏁 Board is now full! Game should be complete!");

        // Step 6: Get final game state via REST API
        console.log("\n📍 Step 6: Getting final game state...");
        const matchResponse = await axios.get(`${apiUrl}/match/${matchId}`);
        const finalMatch = matchResponse.data.match;
          console.log("\n📊 Final Match State:");
        console.log(`   Status: ${finalMatch.status}`);
        console.log(`   Player 1 Score: ${finalMatch.player1Score}`);
        console.log(`   Player 2 Score: ${finalMatch.player2Score}`);
        console.log(`   Winner: ${finalMatch.winnerId || 'None (still playing)'}`);
        console.log(`   Current Turn: ${finalMatch.currentPlayerTurn || 'Game Over'}`);
        console.log(`   Cards Placed: ${matchResponse.data.placements.length}/9`);
        
        console.log("\n🎴 Board State:");
        const board = Array(3).fill(null).map(() => Array(3).fill(null));
        matchResponse.data.placements.forEach(p => {
            board[p.y][p.x] = {
                name: p.card.name,
                owner: p.owner
            };
        });
        
        for (let y = 0; y < 3; y++) {
            let row = "";
            for (let x = 0; x < 3; x++) {
                if (board[y][x]) {
                    const owner = board[y][x].owner === player1Id ? "P1" : "P2";
                    row += `[${owner}:${board[y][x].name.substring(0, 4)}] `;
                } else {
                    row += "[      ] ";
                }
            }
            console.log(`   ${row}`);
        }

        console.log("\n✅ Full game flow test completed!");

    } catch (error) {
        console.error("\n❌ Error:", error.response?.data || error.message);
        if (error.response?.data) {
            console.error("   Details:", JSON.stringify(error.response.data, null, 2));
        }
    } finally {
        if (connection1) {
            await connection1.stop();
        }
        if (connection2) {
            await connection2.stop();
        }
        console.log("\n🔌 Connections closed");
    }
}

function sleep(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

// Run the test
testFullGameFlow().catch(console.error);
