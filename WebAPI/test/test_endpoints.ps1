# Phase 5 Test Script
$ErrorActionPreference = "Continue"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "WebAPI - Phase 5 End-to-End Test" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Test 1: Health endpoint
Write-Host "[Test 1] DeepSeek - /health" -ForegroundColor Yellow
Write-Host "-------------------------------------------" -ForegroundColor Gray
try {
    $result = Invoke-RestMethod -Uri "http://127.0.0.1:55555/health" -Method Get -TimeoutSec 30
    Write-Host "SUCCESS" -ForegroundColor Green
    $result | ConvertTo-Json -Depth 10
} catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
}
Write-Host ""

# Test 2: /v1/models endpoint
Write-Host "[Test 2] DeepSeek - /v1/models" -ForegroundColor Yellow
Write-Host "-------------------------------------------" -ForegroundColor Gray
try {
    $result = Invoke-RestMethod -Uri "http://127.0.0.1:55555/v1/models" -Method Get -TimeoutSec 30
    Write-Host "SUCCESS" -ForegroundColor Green
    Write-Host "Models: $($result.data.id -join ', ')"
} catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
}
Write-Host ""

# Test 3: DeepSeek POST /v1/chat/completions (non-stream)
Write-Host "[Test 3] DeepSeek - POST /v1/chat/completions (non-stream)" -ForegroundColor Yellow
Write-Host "-------------------------------------------" -ForegroundColor Gray
try {
    $msg = "Hello, please reply briefly"
    $body = @{
        model = "deepseek-chat"
        messages = @(@{role = "user"; content = $msg})
        stream = $false
    } | ConvertTo-Json -Depth 10
    
    $result = Invoke-RestMethod -Uri "http://127.0.0.1:55555/v1/chat/completions" -Method Post -ContentType "application/json" -Body $body -TimeoutSec 60
    Write-Host "SUCCESS" -ForegroundColor Green
    Write-Host "Response:" -ForegroundColor Gray
    $result | ConvertTo-Json -Depth 10
} catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
}
Write-Host ""

# Test 4: Qwen POST /v1/chat/completions (non-stream)
Write-Host "[Test 4] Qwen - POST /v1/chat/completions (non-stream)" -ForegroundColor Yellow
Write-Host "-------------------------------------------" -ForegroundColor Gray
try {
    $msg = "Hello, please reply briefly"
    $body = @{
        model = "qwen-turbo"
        messages = @(@{role = "user"; content = $msg})
        stream = $false
    } | ConvertTo-Json -Depth 10
    
    $result = Invoke-RestMethod -Uri "http://127.0.0.1:56666/v1/chat/completions" -Method Post -ContentType "application/json" -Body $body -TimeoutSec 60
    Write-Host "SUCCESS" -ForegroundColor Green
    Write-Host "Response:" -ForegroundColor Gray
    $result | ConvertTo-Json -Depth 10
} catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
}
Write-Host ""

# Test 5: Doubao POST /v1/chat/completions (non-stream)
Write-Host "[Test 5] Doubao - POST /v1/chat/completions (non-stream)" -ForegroundColor Yellow
Write-Host "-------------------------------------------" -ForegroundColor Gray
try {
    $msg = "Hello, please reply briefly"
    $body = @{
        model = "doubao-pro"
        messages = @(@{role = "user"; content = $msg})
        stream = $false
    } | ConvertTo-Json -Depth 10
    
    $result = Invoke-RestMethod -Uri "http://127.0.0.1:55556/v1/chat/completions" -Method Post -ContentType "application/json" -Body $body -TimeoutSec 60
    Write-Host "SUCCESS" -ForegroundColor Green
    Write-Host "Response:" -ForegroundColor Gray
    $result | ConvertTo-Json -Depth 10
} catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
}
Write-Host ""

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Test Complete" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
