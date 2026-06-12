#!/usr/bin/env python3
"""
测试代理服务的流式输出性能
验证是否存在缓冲或阻塞问题
"""

import os
import sys
import time
import json
import requests
from datetime import datetime

API_URL = os.environ.get("API_URL", "https://localhost:8443")
API_KEY = os.environ.get("API_KEY", "change-me")

def format_timestamp():
    """返回毫秒级时间戳"""
    return datetime.now().strftime("%H:%M:%S.%f")[:-3]

def test_streaming_latency():
    """测试流式输出的延迟"""
    print("=== 流式输出延迟测试 ===")
    print(f"API地址: {API_URL}")
    print(f"测试时间: {format_timestamp()}")
    print()
    
    url = f"{API_URL}/v1/chat/completions"
    headers = {
        "Authorization": f"Bearer {API_KEY}",
        "Content-Type": "application/json"
    }
    payload = {
        "model": "gpt-4o",
        "messages": [
            {"role": "user", "content": "请从1数到5，每个数字单独一行输出。"}
        ],
        "stream": True
    }
    
    print(f"[{format_timestamp()}] 发送请求...")
    request_start = time.time()
    
    try:
        with requests.post(
            url, 
            headers=headers, 
            json=payload, 
            stream=True, 
            verify=False,  # 本地开发环境可能使用自签名证书
            timeout=30
        ) as response:
            print(f"[{format_timestamp()}] 收到响应头 (耗时: {(time.time() - request_start)*1000:.0f}ms)")
            print(f"[{format_timestamp()}] Content-Type: {response.headers.get('Content-Type')}")
            print()
            
            first_data_received = False
            line_count = 0
            timestamps = []
            
            for line in response.iter_lines():
                if not line:
                    continue
                    
                current_time = time.time()
                elapsed = (current_time - request_start) * 1000
                timestamps.append(elapsed)
                
                line_text = line.decode('utf-8')
                
                if not first_data_received and line_text.startswith("data:"):
                    print(f"[{format_timestamp()}] 首个data行 (TTFT: {elapsed:.0f}ms)")
                    first_data_received = True
                
                # 打印每一行及其时间戳
                print(f"[{format_timestamp()}] +{elapsed:6.0f}ms | {line_text[:80]}")
                line_count += 1
                
            print()
            print("=== 测试结果分析 ===")
            print(f"总行数: {line_count}")
            print(f"总耗时: {(time.time() - request_start)*1000:.0f}ms")
            
            if len(timestamps) >= 2:
                # 计算时间间隔
                intervals = [timestamps[i+1] - timestamps[i] for i in range(len(timestamps)-1)]
                avg_interval = sum(intervals) / len(intervals) if intervals else 0
                max_interval = max(intervals) if intervals else 0
                
                print(f"平均行间隔: {avg_interval:.0f}ms")
                print(f"最大行间隔: {max_interval:.0f}ms")
                print()
                
                # 判断是否存在批量缓冲
                if max_interval > avg_interval * 5 and max_interval > 500:
                    print("⚠️  警告: 检测到异常的长时间间隔，可能存在缓冲问题")
                    print(f"   最大间隔({max_interval:.0f}ms)远大于平均值({avg_interval:.0f}ms)")
                elif len(set(int(t/100) for t in timestamps[:5])) <= 2:
                    print("⚠️  警告: 前几行时间戳过于接近，可能存在批量输出")
                else:
                    print("✅ 流式输出正常：数据逐行到达，无明显缓冲")
            
    except requests.exceptions.RequestException as e:
        print(f"❌ 请求失败: {e}")
        return False
    except KeyboardInterrupt:
        print("\n测试中断")
        return False
    
    return True

def test_responses_protocol():
    """测试 /v1/responses 端点的流式输出"""
    print()
    print("=== 测试 Responses 协议端点 ===")
    print(f"API地址: {API_URL}")
    print()
    
    url = f"{API_URL}/v1/responses"
    headers = {
        "Authorization": f"Bearer {API_KEY}",
        "Content-Type": "application/json"
    }
    payload = {
        "model": "gpt-4o",
        "messages": [
            {"role": "user", "content": "Say hello"}
        ],
        "stream": True
    }
    
    print(f"[{format_timestamp()}] 发送请求到 /v1/responses...")
    request_start = time.time()
    
    try:
        with requests.post(
            url, 
            headers=headers, 
            json=payload, 
            stream=True, 
            verify=False,
            timeout=30
        ) as response:
            ttft = None
            for line in response.iter_lines():
                if not line:
                    continue
                    
                elapsed = (time.time() - request_start) * 1000
                line_text = line.decode('utf-8')
                
                if ttft is None and "response.output_text.delta" in line_text:
                    ttft = elapsed
                    print(f"[{format_timestamp()}] 首个文本delta (TTFT: {ttft:.0f}ms)")
                
                print(f"[{format_timestamp()}] +{elapsed:6.0f}ms | {line_text[:80]}")
            
            print()
            if ttft:
                print(f"✅ Responses协议 TTFT: {ttft:.0f}ms")
            
    except requests.exceptions.RequestException as e:
        print(f"❌ 请求失败: {e}")
        return False
    
    return True

if __name__ == "__main__":
    # 禁用SSL警告（仅用于本地测试）
    import urllib3
    urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)
    
    print("OpenCodex 流式输出诊断工具")
    print("=" * 60)
    print()
    
    # 测试Chat Completions端点
    success1 = test_streaming_latency()
    
    # 测试Responses端点
    success2 = test_responses_protocol()
    
    print()
    print("=" * 60)
    if success1 and success2:
        print("✅ 所有测试通过")
        sys.exit(0)
    else:
        print("❌ 部分测试失败")
        sys.exit(1)

