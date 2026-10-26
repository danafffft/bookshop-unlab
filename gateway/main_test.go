package main

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestStaticSecurity(t *testing.T) {
	root := t.TempDir()
	if err := os.WriteFile(filepath.Join(root, "index.html"), []byte("Cherry Books"), 0600); err != nil {
		t.Fatal(err)
	}
	t.Setenv("WEB_ROOT", root)
	response := httptest.NewRecorder()
	handler().ServeHTTP(response, httptest.NewRequest("GET", "http://shop.test/", nil))
	if response.Code != 200 || !strings.Contains(response.Body.String(), "Cherry Books") {
		t.Fatalf("unexpected response: %d", response.Code)
	}
	for _, h := range []string{"Content-Security-Policy", "X-Content-Type-Options", "X-Frame-Options", "Referrer-Policy"} {
		if response.Header().Get(h) == "" {
			t.Errorf("missing %s", h)
		}
	}
}
func TestStaticRejectsMutation(t *testing.T) {
	r := httptest.NewRecorder()
	handler().ServeHTTP(r, httptest.NewRequest(http.MethodPost, "http://shop.test/", nil))
	if r.Code != 405 {
		t.Fatalf("got %d", r.Code)
	}
}
func TestStaticDoesNotExposeOutsideRoot(t *testing.T) {
	root := t.TempDir()
	web := filepath.Join(root, "web")
	os.Mkdir(web, 0700)
	os.WriteFile(filepath.Join(root, "secret.txt"), []byte("private"), 0600)
	t.Setenv("WEB_ROOT", web)
	r := httptest.NewRecorder()
	handler().ServeHTTP(r, httptest.NewRequest("GET", "http://shop.test/%2e%2e/secret.txt", nil))
	if strings.Contains(r.Body.String(), "private") {
		t.Fatal("outside file served")
	}
}
