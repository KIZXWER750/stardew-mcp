package main

// Persistent JSON-lines host. Each goal owns an isolated agent worker.
import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"
)

type uiRequest struct {
	Type string `json:"type"`
	ID   string `json:"id"`
	Goal string `json:"goal"`
}
type uiEvent struct {
	Type string `json:"type"`
	ID   string `json:"id"`
	Text string `json:"text"`
}

var uiOutput sync.Mutex

func emitUI(kind, id, message string) {
	uiOutput.Lock()
	defer uiOutput.Unlock()
	_ = json.NewEncoder(os.Stdout).Encode(uiEvent{kind, id, message})
}
func isAICallLog(line string) bool { return strings.Contains(line, "[AI API CALL]") }
func killUIWorker(cmd *exec.Cmd) {
	if cmd == nil || cmd.Process == nil {
		return
	}
	if runtime.GOOS == "windows" {
		_ = exec.Command("taskkill", "/PID", strconv.Itoa(cmd.Process.Pid), "/T", "/F").Run()
	}
	_ = cmd.Process.Kill()
}
func validUIGoal(r uiRequest) bool {
	return r.ID != "" && len(r.ID) <= 80 && strings.TrimSpace(r.Goal) != "" && len(r.Goal) <= 16000 && !strings.Contains(r.Goal, "[SLEEP_VERIFIED]")
}
func runIngameHost() {
	requests := make(chan uiRequest)
	go func() {
		defer close(requests)
		s := bufio.NewScanner(os.Stdin)
		s.Buffer(make([]byte, 4096), 128*1024)
		for s.Scan() {
			var r uiRequest
			if json.Unmarshal(s.Bytes(), &r) == nil {
				requests <- r
			}
		}
	}()
	events := make(chan uiEvent, 256)
	var worker *exec.Cmd
	var runID string
	seen := make(map[string]bool)
	stop := func() { killUIWorker(worker); worker = nil }
	defer stop()
	config, configErr := loadAIConfig()
	readyText := config.description()
	if configErr != nil {
		readyText = configErr.Error()
	}
	emitUI("ready", "", readyText)
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	var started time.Time
	for {
		select {
		case r, ok := <-requests:
			if !ok {
				return
			}
			switch r.Type {
			case "shutdown":
				return
			case "cancel":
				if worker != nil && r.ID == runID {
					stop()
					emitUI("done", runID, "Cancelled. Completed world changes are retained.")
				}
			case "start":
				if _, err := loadAIConfig(); err != nil {
					emitUI("rejected", r.ID, err.Error())
					continue
				}
				if worker != nil || seen[r.ID] || !validUIGoal(r) {
					emitUI("rejected", r.ID, "Busy, duplicate request, invalid goal or unsupported legacy token.")
					continue
				}
				seen[r.ID] = true
				runID = r.ID
				exe, err := os.Executable()
				if err != nil {
					emitUI("done", runID, err.Error())
					continue
				}
				goal := r.Goal + "\nUse normal gameplay only. Never enable cheats. In your final answer, report the requested verified information concisely so it can be shown in the in-game chat. A persisted future day, future time, or crop-growth step is GOAL WAITING, not TASK_BLOCKED; leave it saved for automatic resume. If a step is blocked, try safe prerequisite recovery within the requested scope first. If still unresolved, begin TASK_BLOCKED: with the reason. If complete, put GOAL COMPLETE on the last line. Stop after this goal."
				cmd := exec.Command(exe, "-goal", goal)
				cmd.Env = append(os.Environ(), "STARDEW_UI_RUN="+runID)
				out, e1 := cmd.StdoutPipe()
				errout, e2 := cmd.StderrPipe()
				if e1 != nil || e2 != nil {
					emitUI("done", runID, "Unable to create worker log pipes")
					continue
				}
				if err = cmd.Start(); err != nil {
					emitUI("done", runID, err.Error())
					continue
				}
				worker = cmd
				started = time.Now()
				id := runID
				emitUI("started", id, "Agent starting")
				var readers sync.WaitGroup
				readers.Add(2)
				scan := func(rd io.Reader) {
					defer readers.Done()
					s := bufio.NewScanner(rd)
					s.Buffer(make([]byte, 4096), 1024*1024)
					for s.Scan() {
						line := s.Text()
						events <- uiEvent{"log", id, line}
						if isAICallLog(line) {
							events <- uiEvent{"ai_call", id, "1"}
						}
						if strings.HasSuffix(line, "[UI WORKER FINISHED] "+id) {
							events <- uiEvent{"finish", id, "Agent turn ended; inspect result above."}
						}
					}
				}
				go scan(out)
				go scan(errout)
				go func() { readers.Wait(); err := cmd.Wait(); events <- uiEvent{"exit", id, fmt.Sprint(err)} }()
			}
		case ev := <-events:
			if worker == nil || ev.ID != runID {
				continue
			}
			if ev.Type == "finish" || ev.Type == "exit" {
				stop()
				emitUI("done", ev.ID, ev.Text)
			} else {
				emitUI(ev.Type, ev.ID, ev.Text)
			}
		case <-ticker.C:
			if worker != nil && time.Since(started) > 30*time.Minute {
				stop()
				emitUI("done", runID, "30 minute host limit reached")
			}
		}
	}
}
