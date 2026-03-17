# AvailableVerificationAlgorithms 功能说明

本库提供常见校验与摘要算法，适用于嵌入式通信、文件校验、数据完整性验证等场景。

## 支持的算法

- CRC16/CRC32（Modbus、CCITT、STM32硬件等）
- LRC（纵向冗余校验）
- SUM（累加校验）
- XOR（异或校验）
- SHA256（安全摘要）
- MD5（文件校验）

## 通用接口

所有方法支持 `byte[]`、`string`、`Stream` 输入，便于不同场景调用。

## 示例

```csharp
using AvailableVerificationAlgorithms;

byte[] data = ...;
string text = "abc";

// LRC
byte lrc = CommonVerificationHelpers.CalcLrc(data);

// SUM
byte sum = CommonVerificationHelpers.CalcSum(data);

// XOR
byte xor = CommonVerificationHelpers.CalcXor(data);

// SHA256
string sha256 = CommonVerificationHelpers.CalcSha256(data);
string sha256Text = CommonVerificationHelpers.CalcSha256(text);

// MD5
string md5 = CommonVerificationHelpers.CalcMd5(data);
string md5Text = CommonVerificationHelpers.CalcMd5(text);
```

## CRC16/CRC32

详见 `Crc16Helpers`、`Crc32Helpers`，支持 Modbus、CCITT、STM32 等多种模式。

## 数据对齐

详见 `Aligning/SixteenBytes.cs`，支持 16 字节对齐与 uint 数组转换。

---

### 注释规范

所有代码均采用“步骤编号 + 为什么 + 风险点”模板，便于理解和维护。
