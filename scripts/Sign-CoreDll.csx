// Usage: dotnet script scripts/Sign-CoreDll.csx -- <dll-path> <private-key-path> <output-sig-path>
using System.Security.Cryptography;

var dllPath = Args[0];
var keyPath = Args[1];
var sigPath = Args[2];

var dllBytes = File.ReadAllBytes(dllPath);
var hash = SHA256.HashData(dllBytes);
var pem = File.ReadAllText(keyPath);

using var rsa = RSA.Create();
rsa.ImportFromPem(pem);
var sig = rsa.SignData(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
File.WriteAllText(sigPath, Convert.ToBase64String(sig));
Console.WriteLine($"Signed {dllPath} -> {sigPath}");
