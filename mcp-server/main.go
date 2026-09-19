package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"os"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

// GameClient manages the WebSocket connection to the Stardew Valley mod
type GameClient struct {
	conn        *websocket.Conn
	mu          sync.RWMutex
	state       *GameState
	responses   map[string]chan *WebSocketResponse
	responsesMu sync.Mutex
	connected   bool
	url         string
}

// GameState represents the current state of the game
type GameState struct {
	Player        PlayerState        `json:"player"`
	Time          TimeState          `json:"time"`
	World         WorldState         `json:"world"`
	Surroundings  SurroundingsState  `json:"surroundings"`
	Map           MapInfo            `json:"map"`
	Quests        []QuestInfo        `json:"quests,omitempty"`
	Relationships []RelationshipInfo `json:"relationships,omitempty"`
	Skills        *SkillsInfo        `json:"skills,omitempty"`
}

type PlayerState struct {
	Name                string          `json:"name"`
	X                   int             `json:"x"`
	Y                   int             `json:"y"`
	Location            string          `json:"location"`
	Energy              float64         `json:"energy"`
	MaxEnergy           int             `json:"maxEnergy"`
	Health              int             `json:"health"`
	MaxHealth           int             `json:"maxHealth"`
	Money               int             `json:"money"`
	CurrentTool         string          `json:"currentTool"`
	CurrentToolIndex    int             `json:"currentToolIndex"`
	FacingDirection     int             `json:"facingDirection"`
	FacingDirectionName string          `json:"facingDirectionName"`
	IsMoving            bool            `json:"isMoving"`
	ShopOpen            bool            `json:"shopOpen"`
	CanMove             bool            `json:"canMove"`
	Inventory           []InventoryItem `json:"inventory"`
}

type TimeState struct {
	TimeOfDay           int    `json:"timeOfDay"`
	TimeString          string `json:"timeString"`
	Day                 int    `json:"day"`
	Season              string `json:"season"`
	Year                int    `json:"year"`
	DayOfWeek           string `json:"dayOfWeek"`
	IsNight             bool   `json:"isNight"`
	MinutesUntilMorning int    `json:"minutesUntilMorning"`
}

type WorldState struct {
	Weather             string `json:"weather"`
	IsOutdoors          bool   `json:"isOutdoors"`
	IsFarm              bool   `json:"isFarm"`
	IsGreenhouse        bool   `json:"isGreenhouse"`
	IsBuildableLocation bool   `json:"isBuildableLocation"`
	LocationType        string `json:"locationType"`
}

type SurroundingsState struct {
	FurnitureInfo         string                `json:"furnitureInfo"`
	AsciiMap              string                `json:"asciiMap"`
	NearbyObjects         []NearbyObject        `json:"nearbyObjects"`
	NearbyTerrainFeatures []NearbyTerrain       `json:"nearbyTerrainFeatures"`
	NearbyNPCs            []NearbyNPC           `json:"nearbyNPCs"`
	NearbyMonsters        []NearbyMonster       `json:"nearbyMonsters"`
	NearbyResourceClumps  []NearbyResourceClump `json:"nearbyResourceClumps"`
	NearbyDebris          []NearbyDebris        `json:"nearbyDebris"`
	NearbyBuildings       []NearbyBuilding      `json:"nearbyBuildings"`
	NearbyAnimals         []NearbyAnimal        `json:"nearbyAnimals"`
	WarpPoints            []WarpPoint           `json:"warpPoints"`
	TileInFront           TileInFront           `json:"tileInFront"`
}

type MapInfo struct {
	Name        string `json:"name"`
	DisplayName string `json:"displayName"`
	Width       int    `json:"width"`
	Height      int    `json:"height"`
	IsMineLevel bool   `json:"isMineLevel"`
	MineLevel   int    `json:"mineLevel"`
	UniqueId    string `json:"uniqueId"`
}

type TileInfo struct {
	X                 int    `json:"x"`
	Y                 int    `json:"y"`
	IsPassable        bool   `json:"isPassable"`
	IsWater           bool   `json:"isWater"`
	IsTillable        bool   `json:"isTillable"`
	HasObject         bool   `json:"hasObject"`
	HasTerrainFeature bool   `json:"hasTerrainFeature"`
	TileType          string `json:"tileType"`
}

type NearbyObject struct {
	X                 int    `json:"x"`
	Y                 int    `json:"y"`
	Name              string `json:"name"`
	DisplayName       string `json:"displayName"`
	Type              string `json:"type"`
	IsPassable        bool   `json:"isPassable"`
	CanBePickedUp     bool   `json:"canBePickedUp"`
	IsReadyForHarvest bool   `json:"isReadyForHarvest"`
	MinutesUntilReady int    `json:"minutesUntilReady"`
	HeldItemName      string `json:"heldItemName,omitempty"`
	RequiredTool      string `json:"requiredTool,omitempty"`
	HitsRequired      int    `json:"hitsRequired"`
}

type NearbyTerrain struct {
	X                          int    `json:"x"`
	Y                          int    `json:"y"`
	Type                       string `json:"type"`
	IsPassable                 bool   `json:"isPassable"`
	GrowthStage                int    `json:"growthStage"`
	IsStump                    bool   `json:"isStump"`
	TreeState                  string `json:"treeState"`
	IsFullyGrown               bool   `json:"isFullyGrown"`
	HasSeed                    bool   `json:"hasSeed"`
	CanBeChopped               bool   `json:"canBeChopped"`
	FruitCount                 int    `json:"fruitCount"`
	IsWatered                  bool   `json:"isWatered"`
	HasCrop                    bool   `json:"hasCrop"`
	CropName                   string `json:"cropName,omitempty"`
	CropPhase                  int    `json:"cropPhase"`
	DaysUntilHarvest           int    `json:"daysUntilHarvest"`
	IsReadyForHarvest          bool   `json:"isReadyForHarvest"`
	IsDead                     bool   `json:"isDead"`
	GrassType                  int    `json:"grassType"`
	RequiredTool               string `json:"requiredTool,omitempty"`
	HitsRequired               int    `json:"hitsRequired"`
	EstimatedTotalHitsToRemove int    `json:"estimatedTotalHitsToRemove"`
	RemovalSequence            string `json:"removalSequence"`
}

type NearbyNPC struct {
	X               int    `json:"x"`
	Y               int    `json:"y"`
	Name            string `json:"name"`
	DisplayName     string `json:"displayName"`
	IsFacingPlayer  bool   `json:"isFacingPlayer"`
	CanTalk         bool   `json:"canTalk"`
	FriendshipLevel int    `json:"friendshipLevel"`
	IsMoving        bool   `json:"isMoving"`
}

type NearbyMonster struct {
	X              int    `json:"x"`
	Y              int    `json:"y"`
	Name           string `json:"name"`
	Health         int    `json:"health"`
	MaxHealth      int    `json:"maxHealth"`
	DamageToFarmer int    `json:"damageToFarmer"`
	IsGlider       bool   `json:"isGlider"`
	Distance       int    `json:"distance"`
}

type NearbyResourceClump struct {
	X            int     `json:"x"`
	Y            int     `json:"y"`
	Width        int     `json:"width"`
	Height       int     `json:"height"`
	Type         string  `json:"type"`
	Health       float64 `json:"health"`
	RequiredTool string  `json:"requiredTool,omitempty"`
	HitsRequired int     `json:"hitsRequired"`
}

type NearbyDebris struct {
	X               int    `json:"x"`
	Y               int    `json:"y"`
	Name            string `json:"name"`
	Type            string `json:"type"`
	CanBePickedUp   bool   `json:"canBePickedUp"`
	QualifiedItemID string `json:"qualifiedItemId,omitempty"`
	Stack           int    `json:"stack,omitempty"`
	Source          string `json:"source,omitempty"`
	ChunkCount      int    `json:"chunkCount,omitempty"`
}

type NearbyBuilding struct {
	X          int    `json:"x"`
	Y          int    `json:"y"`
	Width      int    `json:"width"`
	Height     int    `json:"height"`
	Type       string `json:"type"`
	DoorX      int    `json:"doorX"`
	DoorY      int    `json:"doorY"`
	HasAnimals bool   `json:"hasAnimals"`
}

type NearbyAnimal struct {
	X          int    `json:"x"`
	Y          int    `json:"y"`
	Name       string `json:"name"`
	Type       string `json:"type"`
	Age        int    `json:"age"`
	Happiness  int    `json:"happiness"`
	CanBePet   bool   `json:"canBePet"`
	HasProduce bool   `json:"hasProduce"`
}

type WarpPoint struct {
	X              int    `json:"x"`
	Y              int    `json:"y"`
	TargetLocation string `json:"targetLocation"`
	TargetX        int    `json:"targetX"`
	TargetY        int    `json:"targetY"`
	IsDoor         bool   `json:"isDoor"`
}

type TileInFront struct {
	X            int    `json:"x"`
	Y            int    `json:"y"`
	IsPassable   bool   `json:"isPassable"`
	IsWater      bool   `json:"isWater"`
	IsTillable   bool   `json:"isTillable"`
	ObjectName   string `json:"objectName,omitempty"`
	ObjectType   string `json:"objectType,omitempty"`
	TerrainType  string `json:"terrainType,omitempty"`
	NPCName      string `json:"npcName,omitempty"`
	CanInteract  bool   `json:"canInteract"`
	RequiredTool string `json:"requiredTool,omitempty"`
}

type InventoryItem struct {
	Slot        int    `json:"slot"`
	Name        string `json:"name"`
	DisplayName string `json:"displayName"`
	Stack       int    `json:"stack"`
	Category    string `json:"category"`
	IsTool      bool   `json:"isTool"`
	IsWeapon    bool   `json:"isWeapon"`
}

type QuestInfo struct {
	ID          string `json:"id"`
	Name        string `json:"name"`
	Description string `json:"description"`
	Objective   string `json:"objective"`
	DaysLeft    int    `json:"daysLeft"`
	Reward      int    `json:"reward"`
	IsComplete  bool   `json:"isComplete"`
}

type RelationshipInfo struct {
	NPCName          string `json:"npcName"`
	FriendshipPoints int    `json:"friendshipPoints"`
	Hearts           int    `json:"hearts"`
	GiftsToday       int    `json:"giftsToday"`
	GiftsThisWeek    int    `json:"giftsThisWeek"`
	TalkedToToday    bool   `json:"talkedToToday"`
	Status           string `json:"status"`
}

type SkillsInfo struct {
	Farming    int `json:"farming"`
	Mining     int `json:"mining"`
	Foraging   int `json:"foraging"`
	Fishing    int `json:"fishing"`
	Combat     int `json:"combat"`
	FarmingXp  int `json:"farmingXp"`
	MiningXp   int `json:"miningXp"`
	ForagingXp int `json:"foragingXp"`
	FishingXp  int `json:"fishingXp"`
	CombatXp   int `json:"combatXp"`
}

type WebSocketMessage struct {
	ID     string                 `json:"id,omitempty"`
	Type   string                 `json:"type"`
	Action string                 `json:"action,omitempty"`
	Params map[string]interface{} `json:"params,omitempty"`
}

type WebSocketResponse struct {
	State   *GameState  `json:"state,omitempty"`
	ID      string      `json:"id,omitempty"`
	Type    string      `json:"type"`
	Success bool        `json:"success"`
	Message string      `json:"message,omitempty"`
	Data    interface{} `json:"data,omitempty"`
}

var gameClient *GameClient

func NewGameClient() *GameClient {
	return &GameClient{
		responses: make(map[string]chan *WebSocketResponse),
	}
}

func (c *GameClient) Connect(url string) error {
	c.url = url
	conn, _, err := websocket.DefaultDialer.Dial(url, nil)
	if err != nil {
		return fmt.Errorf("failed to connect to game: %w", err)
	}

	c.mu.Lock()
	c.conn = conn
	c.connected = true
	c.mu.Unlock()

	go c.listen()
	go c.keepAlive()

	return nil
}

func (c *GameClient) keepAlive() {
	ticker := time.NewTicker(15 * time.Second)
	defer ticker.Stop()

	for range ticker.C {
		c.mu.RLock()
		conn := c.conn
		connected := c.connected
		c.mu.RUnlock()

		if !connected || conn == nil {
			return
		}

		ping := WebSocketMessage{
			ID:   fmt.Sprintf("%d", time.Now().UnixNano()),
			Type: "ping",
		}
		data, _ := json.Marshal(ping)

		c.mu.Lock()
		err := conn.WriteMessage(websocket.TextMessage, data)
		c.mu.Unlock()

		if err != nil {
			log.Printf("Ping failed: %v", err)
			return
		}
	}
}

func (c *GameClient) reconnect() {
	c.mu.Lock()
	c.connected = false
	if c.conn != nil {
		c.conn.Close()
	}
	c.mu.Unlock()

	for {
		log.Printf("Attempting to reconnect...")
		time.Sleep(5 * time.Second)

		conn, _, err := websocket.DefaultDialer.Dial(c.url, nil)
		if err != nil {
			log.Printf("Reconnect failed: %v", err)
			continue
		}

		c.mu.Lock()
		c.conn = conn
		c.connected = true
		c.mu.Unlock()

		log.Printf("Reconnected to Stardew Valley at %s", c.url)

		go c.listen()
		go c.keepAlive()
		return
	}
}

func (c *GameClient) listen() {
	for {
		c.mu.RLock()
		conn := c.conn
		c.mu.RUnlock()

		if conn == nil {
			return
		}

		_, message, err := conn.ReadMessage()
		if err != nil {
			log.Printf("WebSocket read error from %s: %v", c.url, err)
			go c.reconnect()
			return
		}

		var response WebSocketResponse
		if err := json.Unmarshal(message, &response); err != nil {
			log.Printf("Failed to parse response: %v", err)
			continue
		}

		switch response.Type {
		case "state":
			c.handleStateUpdate(&response)
		case "response":
			c.handleCommandResponse(&response)
		case "pong":
			// Heartbeat response, ignore
		case "error":
			log.Printf("Error from game: %s", response.Message)
		}
	}
}

func (c *GameClient) handleStateUpdate(response *WebSocketResponse) {
	data, err := json.Marshal(response.Data)
	if err != nil {
		log.Printf("Failed to marshal state data: %v", err)
		return
	}

	var state GameState
	if err := json.Unmarshal(data, &state); err != nil {
		log.Printf("Failed to parse state: %v", err)
		return
	}

	c.mu.Lock()
	c.state = &state
	c.mu.Unlock()
}

func (c *GameClient) handleCommandResponse(response *WebSocketResponse) {
	if response.ID == "" {
		return
	}

	c.responsesMu.Lock()
	ch, ok := c.responses[response.ID]
	if ok {
		delete(c.responses, response.ID)
	}
	c.responsesMu.Unlock()

	if ok {
		// Publish the command-time snapshot BEFORE waking the waiting tool.
		if response.State != nil {
			c.mu.Lock()
			c.state = response.State
			c.mu.Unlock()
		}
		ch <- response
	}
}

func (c *GameClient) GetState() *GameState {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.state
}

func (c *GameClient) IsConnected() bool {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.connected
}

func (c *GameClient) SendCommand(action string, params map[string]interface{}) (*WebSocketResponse, error) {
	if token := os.Getenv("STARDEW_UI_RUN"); token != "" {
		copyParams := make(map[string]interface{})
		for k, v := range params {
			copyParams[k] = v
		}
		copyParams["_uiRun"] = token
		params = copyParams
	}
	if !c.IsConnected() {
		return nil, fmt.Errorf("not connected to game")
	}

	id := fmt.Sprintf("%d", time.Now().UnixNano())

	msg := WebSocketMessage{
		ID:     id,
		Type:   "command",
		Action: action,
		Params: params,
	}

	ch := make(chan *WebSocketResponse, 1)
	c.responsesMu.Lock()
	c.responses[id] = ch
	c.responsesMu.Unlock()

	data, err := json.Marshal(msg)
	if err != nil {
		return nil, err
	}

	c.mu.Lock()
	err = c.conn.WriteMessage(websocket.TextMessage, data)
	c.mu.Unlock()

	if err != nil {
		c.responsesMu.Lock()
		delete(c.responses, id)
		c.responsesMu.Unlock()
		return nil, err
	}

	// Timeout for command responses (15 seconds is sufficient for most operations)
	select {
	case response := <-ch:
		return response, nil
	case <-time.After(15 * time.Second):
		c.responsesMu.Lock()
		delete(c.responses, id)
		c.responsesMu.Unlock()
		return nil, fmt.Errorf("timeout waiting for response")
	}
}

func main() {
	if len(os.Args) > 1 && os.Args[1] == "--check-ai-config" {
		cfg, err := loadAIConfig()
		if err != nil {
			log.Print(err)
			os.Exit(1)
		}
		log.Printf("[AI CONFIG] %s; configuration valid (no API request or game connection made)", cfg.description())
		return
	}
	if len(os.Args) > 1 && os.Args[1] == "--ingame-host" {
		runIngameHost()
		return
	}
	autoFlag := flag.Bool("auto", true, "Start in autonomous mode")
	goalFlag := flag.String("goal", "", "Goal for autonomous mode; empty means no actions")
	urlFlag := flag.String("url", "ws://localhost:8765/game", "WebSocket URL for the game mod")
	flag.Parse()
	if *autoFlag && *goalFlag != "" {
		if _, err := loadAIConfig(); err != nil {
			log.Print(err)
			os.Exit(1)
		}
	}

	gameClient = NewGameClient()

	go func() {
		for {
			if err := gameClient.Connect(*urlFlag); err != nil {
				log.Printf("Failed to connect to game (will retry): %v", err)
				time.Sleep(5 * time.Second)
				continue
			}
			log.Println("Connected to Stardew Valley!")

			if *autoFlag && *goalFlag != "" {
				log.Printf("Starting autonomous agent with goal: %s", *goalFlag)

				agent, err := NewStardewAgent()
				if err != nil {
					log.Printf("Failed to start agent: %v", err)
					if os.Getenv("STARDEW_UI_RUN") != "" {
						os.Exit(1)
					}
					return
				}
				if err := agent.StartSession(*goalFlag); err != nil {
					log.Printf("Failed to start session: %v", err)
					if os.Getenv("STARDEW_UI_RUN") != "" {
						os.Exit(1)
					}
					return
				}
			}
			break
		}
	}()

	// Block forever
	select {}
}
