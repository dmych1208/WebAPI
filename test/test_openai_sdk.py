"""
WebAPI 端到端测试脚本
测试三个渠道的流式/非流式请求
"""

from openai import OpenAI
import time

def test_deepseek():
    print("=" * 50)
    print("测试 DeepSeek 渠道")
    print("=" * 50)
    
    client = OpenAI(base_url="http://127.0.0.1:55555/v1", api_key="sk-any")
    
    print("\n[非流式测试]")
    start = time.time()
    response = client.chat.completions.create(
        model="deepseek-chat",
        messages=[{"role": "user", "content": "你好，请简短回复"}],
        stream=False
    )
    print(f"响应: {response.choices[0].message.content}")
    print(f"耗时: {time.time() - start:.2f}s")
    
    print("\n[流式测试]")
    start = time.time()
    stream = client.chat.completions.create(
        model="deepseek-chat",
        messages=[{"role": "user", "content": "请用一句话介绍你自己"}],
        stream=True
    )
    print("响应: ", end="", flush=True)
    for chunk in stream:
        if chunk.choices[0].delta.content:
            print(chunk.choices[0].delta.content, end="", flush=True)
    print(f"\n耗时: {time.time() - start:.2f}s")

def test_qwen():
    print("\n" + "=" * 50)
    print("测试 通义千问 渠道")
    print("=" * 50)
    
    client = OpenAI(base_url="http://127.0.0.1:56666/v1", api_key="sk-any")
    
    print("\n[非流式测试]")
    start = time.time()
    response = client.chat.completions.create(
        model="qwen-turbo",
        messages=[{"role": "user", "content": "你好，请简短回复"}],
        stream=False
    )
    print(f"响应: {response.choices[0].message.content}")
    print(f"耗时: {time.time() - start:.2f}s")

def test_doubao():
    print("\n" + "=" * 50)
    print("测试 豆包 渠道")
    print("=" * 50)
    
    client = OpenAI(base_url="http://127.0.0.1:55556/v1", api_key="sk-any")
    
    print("\n[非流式测试]")
    start = time.time()
    response = client.chat.completions.create(
        model="doubao-pro",
        messages=[{"role": "user", "content": "你好，请简短回复"}],
        stream=False
    )
    print(f"响应: {response.choices[0].message.content}")
    print(f"耗时: {time.time() - start:.2f}s")

def test_boundary():
    print("\n" + "=" * 50)
    print("边界情况测试")
    print("=" * 50)
    
    client = OpenAI(base_url="http://127.0.0.1:55555/v1", api_key="sk-any")
    
    print("\n[特殊字符测试]")
    response = client.chat.completions.create(
        model="deepseek-chat",
        messages=[{"role": "user", "content": "你好 🎉 测试特殊字符: <>&\"' 中文English 混合"}],
        stream=False
    )
    print(f"响应: {response.choices[0].message.content[:100]}...")
    
    print("\n[超长文本测试]")
    long_text = "请总结以下内容: " + "这是一段测试文本。" * 100
    response = client.chat.completions.create(
        model="deepseek-chat",
        messages=[{"role": "user", "content": long_text}],
        stream=False
    )
    print(f"响应: {response.choices[0].message.content[:100]}...")

if __name__ == "__main__":
    print("WebAPI 端到端测试开始")
    print("请确保 WebAPI 服务已启动")
    print()
    
    try:
        test_deepseek()
    except Exception as e:
        print(f"DeepSeek 测试失败: {e}")
    
    try:
        test_qwen()
    except Exception as e:
        print(f"通义千问 测试失败: {e}")
    
    try:
        test_doubao()
    except Exception as e:
        print(f"豆包 测试失败: {e}")
    
    try:
        test_boundary()
    except Exception as e:
        print(f"边界测试失败: {e}")
    
    print("\n" + "=" * 50)
    print("测试完成")
    print("=" * 50)
