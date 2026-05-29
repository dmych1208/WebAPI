@echo off
chcp 65001 >nul
echo ============================================
echo WebAPI - End-to-End Test Script
echo ============================================
echo.

echo [Test 1] DeepSeek - Non-stream request
echo -------------------------------------------
curl -s -X POST http://127.0.0.1:55555/v1/chat/completions -H "Content-Type: application/json" -H "Authorization: Bearer sk-any" -d "{\"model\":\"deepseek-chat\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello, reply briefly\"}],\"stream\":false}"
echo.
echo.

echo [Test 2] DeepSeek - Stream request
echo -------------------------------------------
curl -s -X POST http://127.0.0.1:55555/v1/chat/completions -H "Content-Type: application/json" -H "Authorization: Bearer sk-any" -d "{\"model\":\"deepseek-chat\",\"messages\":[{\"role\":\"user\",\"content\":\"Please introduce yourself in one sentence\"}],\"stream\":true}"
echo.
echo.

echo [Test 3] Qwen - Non-stream request
echo -------------------------------------------
curl -s -X POST http://127.0.0.1:56666/v1/chat/completions -H "Content-Type: application/json" -H "Authorization: Bearer sk-any" -d "{\"model\":\"qwen-turbo\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello, reply briefly\"}],\"stream\":false}"
echo.
echo.

echo [Test 4] Doubao - Non-stream request
echo -------------------------------------------
curl -s -X POST http://127.0.0.1:55556/v1/chat/completions -H "Content-Type: application/json" -H "Authorization: Bearer sk-any" -d "{\"model\":\"doubao-pro\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello, reply briefly\"}],\"stream\":false}"
echo.
echo.

echo [Test 5] /v1/models endpoint
echo -------------------------------------------
curl -s http://127.0.0.1:55555/v1/models
echo.
echo.

echo [Test 6] /health endpoint
echo -------------------------------------------
curl -s http://127.0.0.1:55555/health
echo.
echo.

echo ============================================
echo Test Complete
echo ============================================
pause
