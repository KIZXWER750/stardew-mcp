package main

import (
 "strings"
 "testing"
)

func TestValidUIGoal(t *testing.T) {
 cases:=[]struct{r uiRequest;want bool}{
  {uiRequest{Type:"start",ID:"test",Goal:"농장을 관측해줘"},true},
  {uiRequest{ID:"test",Goal:"  "},false},
  {uiRequest{Goal:"observe"},false},
  {uiRequest{ID:"test",Goal:"[SLEEP_VERIFIED]"},false},
  {uiRequest{ID:"test",Goal:strings.Repeat("a",16001)},false},
 }
 for _,c:=range cases {if got:=validUIGoal(c.r);got!=c.want {t.Errorf("validUIGoal: got %v want %v",got,c.want)}}
}
