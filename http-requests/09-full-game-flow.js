// Full game flow test using both REST API and SignalR
// Run this with: node 09-full-game-flow.js
// First install: npm install @microsoft/signalr axios

const signalR = require("@microsoft/signalr");
const axios = require("axios");

const baseUrl = "http://localhost:5041";
const apiUrl = `${baseUrl}/api/game`;
const hubUrl = `${baseUrl}/gamehub`;

const suffix = Date.now();

const player1Id = `test-player-1-${suffix}`;
const player2Id = `test-player-2-${suffix}`;
const player1Email = `${player1Id}@example.com`;
const player2Email = `${player2Id}@example.com`;
const password = "Password123";

let matchId;
let player1Cards = [];
let player2Cards = [];
let cardNamesMap = {}; // Map card IDs to names

// Register the test players (ignores "already taken") and sign in to get JWTs.
async function ensurePlayer(login, email) {
    try {
        await axios.post(`${baseUrl}/api/player/register`, { login, email, password });
    } catch (error) {
        if (!error.response || error.response.status !== 409) throw error;
    }

    const res = await axios.post(`${baseUrl}/api/player/sign-in`, {
        identifier: login,
        password,
    });
    return res.data.token;
}

async function createConnection(playerId, accessToken) {
    const connection = new signalR.HubConnectionBuilder()
        .withUrl(hubUrl, { accessTokenProvider: () => accessToken })
        .withAutomaticReconnect()
        .configureLogging(signalR.LogLevel.Warning)
        .build();
    // Set up event listeners
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
        // Step 0: Register + sign in both players to get JWTs
        console.log("📍 Step 0: Authenticating players...");
        const token1 = await ensurePlayer(player1Id, player1Email);
        const token2 = await ensurePlayer(player2Id, player2Email);
        console.log("✅ Both players authenticated\n");

        const auth1 = { Authorization: `Bearer ${token1}` };
        const auth2 = { Authorization: `Bearer ${token2}` };

        // Step 1: Create match via REST API (identity comes from the JWT)
        console.log("📍 Step 1: Creating match via REST API...");
        const createResponse = await axios.post(
            `${apiUrl}/match`,
            { opponentId: player2Id },
            { headers: auth1 }
        );
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
        const handResponse = await axios.get(`${apiUrl}/match/${matchId}/hand`, {
            headers: auth2,
        });
        player2Cards = handResponse.data.filter(c => !c.isUsed);

        // Add player 2's cards to the map
        player2Cards.forEach(card => {
            cardNamesMap[card.id] = card.name;
        });

        console.log(`✅ Player 2 cards:`, player2Cards.map(c => `${c.name} (ID: ${c.id})`).join(", "));
        console.log();

        // Step 3: Connect both players to SignalR with their JWTs
        console.log("📍 Step 3: Connecting players to SignalR...");
        connection1 = await createConnection("Player1", token1);
        connection2 = await createConnection("Player2", token2);
        console.log("✅ Both players connected\n");        // Step 4: Join match groups
        console.log("📍 Step 4: Joining match groups...");
        await connection1.invoke("JoinMatch", matchId);
        await connection2.invoke("JoinMatch", matchId);
        console.log("✅ Both players joined match group\n");

        await sleep(1000);        // Step 5: Play cards alternately via SignalR - FULL GAME (9 cards)
        console.log("📍 Step 5: Playing COMPLETE game via SignalR (9 cards)...");
        console.log("=" .repeat(60));        // Player 1 plays card at (0,0)
        console.log(`\n🎯 Player 1's turn - Playing ${player1Cards[0].name} at (0,0)`);
        await connection1.invoke("PlayCard", matchId, player1Cards[0].id, 0, 0, token1);
        await sleep(1000);

        // Player 2 plays card at (1,0)
        console.log(`\n🎯 Player 2's turn - Playing ${player2Cards[0].name} at (1,0)`);
        await connection2.invoke("PlayCard", matchId, player2Cards[0].id, 1, 0, token2);
        await sleep(1000);

        // Player 1 plays card at (0,1)
        console.log(`\n🎯 Player 1's turn - Playing ${player1Cards[1].name} at (0,1)`);
        await connection1.invoke("PlayCard", matchId, player1Cards[1].id, 0, 1, token1);
        await sleep(1000);

        // Player 2 plays card at (2,0)
        console.log(`\n🎯 Player 2's turn - Playing ${player2Cards[1].name} at (2,0)`);
        await connection2.invoke("PlayCard", matchId, player2Cards[1].id, 2, 0, token2);
        await sleep(1000);

        // Player 1 plays card at (1,1)
        console.log(`\n🎯 Player 1's turn - Playing ${player1Cards[2].name} at (1,1)`);
        await connection1.invoke("PlayCard", matchId, player1Cards[2].id, 1, 1, token1);
        await sleep(1000);

        // Player 2 plays card at (2,1)
        console.log(`\n🎯 Player 2's turn - Playing ${player2Cards[2].name} at (2,1)`);
        await connection2.invoke("PlayCard", matchId, player2Cards[2].id, 2, 1, token2);
        await sleep(1000);

        // Player 1 plays card at (0,2)
        console.log(`\n🎯 Player 1's turn - Playing ${player1Cards[3].name} at (0,2)`);
        await connection1.invoke("PlayCard", matchId, player1Cards[3].id, 0, 2, token1);
        await sleep(1000);

        // Player 2 plays card at (1,2)
        console.log(`\n🎯 Player 2's turn - Playing ${player2Cards[3].name} at (1,2)`);
        await connection2.invoke("PlayCard", matchId, player2Cards[3].id, 1, 2, token2);
        await sleep(1000);

        // Player 1 plays card at (2,2) - FINAL CARD!
        console.log(`\n🎯 Player 1's turn - Playing ${player1Cards[4].name} at (2,2) - FINAL CARD!`);
        await connection1.invoke("PlayCard", matchId, player1Cards[4].id, 2, 2, token1);
        await sleep(1500);

        console.log("\n" + "=" .repeat(60));
        console.log("🏁 Board is now full! Game should be complete!");

        // Step 6: Get final game state via REST API
        console.log("\n📍 Step 6: Getting final game state...");
        const matchResponse = await axios.get(`${apiUrl}/match/${matchId}`, {
            headers: auth1,
        });
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

        // Step 6b: Verify coins/XP + W/L/T were awarded (win 200/50, tie 80/15, loss 20/0)
        console.log("\n📍 Step 6b: Checking player stats after the match...");
        const p1Profile = (await axios.get(`${baseUrl}/api/player/me`, { headers: auth1 })).data;
        const p2Profile = (await axios.get(`${baseUrl}/api/player/me`, { headers: auth2 })).data;

        const isDraw = !finalMatch.winnerId;
        const p1Won = finalMatch.winnerId === player1Id;

        const expectedFor = (isWinner) =>
            isDraw
                ? { coins: 80, experience: 15, wins: 0, losses: 0, ties: 1 }
                : isWinner
                  ? { coins: 200, experience: 50, wins: 1, losses: 0, ties: 0 }
                  : { coins: 20, experience: 0, wins: 0, losses: 1, ties: 0 };

        const checkReward = (label, profile, exp) => {
            const ok =
                profile.coins === exp.coins &&
                profile.experience === exp.experience &&
                profile.wins === exp.wins &&
                profile.losses === exp.losses &&
                profile.ties === exp.ties;
            console.log(
                `   ${ok ? "✅" : "❌"} ${label}: ${profile.coins} coins, ${profile.experience} XP, ` +
                    `${profile.wins}W/${profile.losses}L/${profile.ties}T` +
                    ` (expected ${exp.coins} coins, ${exp.experience} XP, ` +
                    `${exp.wins}W/${exp.losses}L/${exp.ties}T)`
            );
        };

        checkReward(player1Id, p1Profile, expectedFor(p1Won));
        checkReward(player2Id, p2Profile, expectedFor(!p1Won && !isDraw));

        console.log("\n✅ Full game flow test completed!");

        // Step 7: Verify REST PlayCard (and hand) is rejected without a token → 401
        console.log("\n📍 Step 7: Checking unauthenticated REST calls → 401...");
        try {
            await axios.post(`${apiUrl}/match/${matchId}/play`, { cardId: 1, x: 0, y: 0 });
            console.log("❌ Expected 401 but PlayCard without token succeeded!");
        } catch (error) {
            console.log(`✅ Unauthenticated PlayCard rejected: ${error.response?.status}`);
        }
        try {
            await axios.get(`${apiUrl}/match/${matchId}/hand`);
            console.log("❌ Expected 401 but getPlayerHand without token succeeded!");
        } catch (error) {
            console.log(`✅ Unauthenticated getPlayerHand rejected: ${error.response?.status}`);
        }

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
