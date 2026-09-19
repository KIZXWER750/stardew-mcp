package main

import (
	"fmt"
	"log"
	"time"
)

func runVerifiedSleep() {
	result, _ := (&StardewAgent{}).sleepUntilMorning()
	log.Printf("[SLEEP RESULT] %s", result)
}

func (a *StardewAgent) sleepUntilMorning() (string, error) {
	a.toolMutex.Lock()
	defer a.toolMutex.Unlock()
	defer func() {
		_, err := gameClient.SendCommand("stop", nil)
		if err != nil {
			log.Printf("[SLEEP STOP ERROR] %v", err)
		}
	}()

	deadline := time.Now().Add(190 * time.Second)
	first := true

	for time.Now().Before(deadline) {
		params := map[string]interface{}{}
		if first {
			params["reset"] = true
			first = false
		}

		response, err := gameClient.SendCommand("sleep_step", params)
		if err != nil {
			log.Printf("[SLEEP BLOCKED] %v", err)
			return "TASK_BLOCKED: " + err.Error(), nil
		}
		if response == nil {
			log.Printf("[SLEEP BLOCKED] No command response")
			return "TASK_BLOCKED: no command response", nil
		}

		log.Printf("[SLEEP] %s", response.Message)
		if !response.Success {
			return "TASK_BLOCKED: " + response.Message, nil
		}
		if response.Message == "SLEEP_VERIFIED: next morning confirmed." {
			log.Printf("[SLEEP COMPLETE] Verified by game state, not an AI claim.")
			state := gameClient.GetState()
			if state == nil {
				return "TASK_BLOCKED: next morning response had no state", nil
			}
			return fmt.Sprintf(`{"status":"COMPLETED","location":%q,"time":%d,"day":%d,"season":%q,"year":%d}`, state.Player.Location, state.Time.TimeOfDay, state.Time.Day, state.Time.Season, state.Time.Year), nil
		}

		time.Sleep(time.Second)
	}
	log.Printf("[SLEEP BLOCKED] Timeout; completion not confirmed.")
	return "TASK_BLOCKED: sleep timeout; completion not confirmed", nil
}
