package main

import (
	"log"
	"time"
)

func runVerifiedSleep() {
	defer func() {
		_, err := gameClient.SendCommand("stop", nil)
		if err != nil {
			log.Printf("[SLEEP STOP ERROR] %v", err)
		}
	}()

	deadline := time.Now().Add(100 * time.Second)
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
			return
		}
		if response == nil {
			log.Printf("[SLEEP BLOCKED] No command response")
			return
		}

		log.Printf("[SLEEP] %s", response.Message)
		if !response.Success {
			return
		}
		if response.Message == "SLEEP_VERIFIED: next morning confirmed." {
			log.Printf("[SLEEP COMPLETE] Verified by game state, not an AI claim.")
			return
		}

		time.Sleep(time.Second)
	}
	log.Printf("[SLEEP BLOCKED] Timeout; completion not confirmed.")
}
