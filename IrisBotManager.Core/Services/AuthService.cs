using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IrisBotManager.Core.Models;

namespace IrisBotManager.Core.Services
{
    public class AuthService
    {
        private string _currentPin = "";
        private readonly Random _random = new();
        private readonly string _adminFilePath;

        // PIN 변경 이벤트
        public event Action<string>? PinChanged;

        public AuthService()
        {
            // 관리자 파일 경로 설정
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var dataDirectory = Path.Combine(baseDirectory, "data");
            Directory.CreateDirectory(dataDirectory);
            _adminFilePath = Path.Combine(dataDirectory, "admins.txt");

            // 초기 PIN 생성
            GenerateNewPin();
        }

        /// <summary>
        /// 현재 PIN 번호
        /// </summary>
        public string CurrentPin => _currentPin;

        #region PIN 관리

        /// <summary>
        /// 새로운 PIN 생성
        /// </summary>
        public void GenerateNewPin()
        {
            _currentPin = _random.Next(100000, 999999).ToString();
            PinChanged?.Invoke(_currentPin);
        }

        /// <summary>
        /// PIN 검증
        /// </summary>
        private bool CheckAdminPin(string inputPin)
        {
            bool isValid = inputPin == _currentPin;
            if (!isValid)
            {
                // 잘못된 PIN 시도 시 새 PIN 생성
                GenerateNewPin();
            }
            return isValid;
        }

        /// <summary>
        /// PIN 검증 (공개 메서드) - UserRole 매개변수 포함
        /// </summary>
        public bool ValidatePin(string pin, UserRole requiredRole = UserRole.Admin)
        {
            return CheckAdminPin(pin);
        }

        /// <summary>
        /// PIN 검증 (오버로드) - UserRole 없음
        /// </summary>
        public bool ValidatePin(string pin)
        {
            return CheckAdminPin(pin);
        }

        #endregion

        #region 권한 확인

        /// <summary>
        /// 사용자 권한 확인 (2개 매개변수)
        /// </summary>
        public bool HasPermission(string userId, UserRole requiredRole)
        {
            var userRole = GetUserRole(userId);
            return userRole.HasPermission(requiredRole);
        }

        /// <summary>
        /// 사용자 권한 확인 (1개 매개변수 - 기본 Admin 권한)
        /// </summary>
        public bool HasPermission(string userId)
        {
            return HasPermission(userId, UserRole.Admin);
        }

        /// <summary>
        /// 사용자 역할 조회
        /// </summary>
        public UserRole GetUserRole(string userId)
        {
            var adminList = GetAdminList();
            if (adminList.Contains(userId))
            {
                return UserRole.Admin;
            }
            return UserRole.User;
        }

        #endregion

        #region GUI 기반 관리자 관리 (기존 방식)

        /// <summary>
        /// 관리자 등록 (PIN 검증 포함)
        /// </summary>
        public string AddAdmin(string userId, string pin)
        {
            if (!CheckAdminPin(pin))
            {
                return "❌ 잘못된 PIN입니다.\n관리자 추가 실패.";
            }

            try
            {
                var adminList = GetAdminList();

                if (adminList.Contains(userId))
                {
                    return $"⚠️ 이미 등록된 관리자입니다.\nID: {userId}";
                }

                adminList.Add(userId);
                SaveAdminList(adminList);

                return $"✅ 관리자 등록 완료!\nID: {userId}";
            }
            catch (Exception ex)
            {
                return $"❌ 관리자 등록 실패: {ex.Message}";
            }
        }

        /// <summary>
        /// 관리자 삭제 (PIN 검증 포함)
        /// </summary>
        public string RemoveAdmin(string userId, string pin)
        {
            if (!CheckAdminPin(pin))
            {
                return "❌ 잘못된 PIN입니다.\n관리자 삭제 실패.";
            }

            try
            {
                var adminList = GetAdminList();

                if (!adminList.Contains(userId))
                {
                    return $"⚠️ 해당 ID는 관리자가 아닙니다.\nID: {userId}";
                }

                adminList.Remove(userId);
                SaveAdminList(adminList);

                return $"🗑️ 관리자 삭제 완료!\nID: {userId}";
            }
            catch (Exception ex)
            {
                return $"❌ 관리자 삭제 실패: {ex.Message}";
            }
        }

        /// <summary>
        /// 관리자 목록 조회 (PIN 검증 포함)
        /// </summary>
        public string ShowAdminList(string pin)
        {
            if (!CheckAdminPin(pin))
            {
                return "❌ 잘못된 PIN입니다.\n관리자 목록 확인 실패.";
            }

            try
            {
                var adminList = GetAdminList();

                if (adminList.Count == 0)
                {
                    return "🔐 등록된 관리자가 없습니다.";
                }

                var result = "🔐 등록된 관리자 목록:\n";
                for (int i = 0; i < adminList.Count; i++)
                {
                    result += $"{i + 1}. {adminList[i]}\n";
                }

                return result.TrimEnd();
            }
            catch (Exception ex)
            {
                return $"❌ 관리자 목록 조회 실패: {ex.Message}";
            }
        }

        #endregion

        #region 채팅 기반 관리자 관리 (새로운 방식)

        /// <summary>
        /// PIN 검증 없이 직접 관리자 등록 (채팅 명령어용)
        /// </summary>
        public string AddAdminDirect(string userId)
        {
            try
            {
                var adminList = GetAdminList();

                if (adminList.Contains(userId))
                {
                    return $"⚠️ 이미 등록된 관리자입니다.\nID: {userId}";
                }

                adminList.Add(userId);
                SaveAdminList(adminList);

                return $"✅ 관리자 등록 완료!\nID: {userId}\n환영합니다! 👑";
            }
            catch (Exception ex)
            {
                return $"❌ 관리자 등록 실패: {ex.Message}";
            }
        }

        /// <summary>
        /// PIN 검증 없이 직접 관리자 삭제 (채팅 명령어용)
        /// </summary>
        public string RemoveAdminDirect(string userId)
        {
            try
            {
                var adminList = GetAdminList();

                if (!adminList.Contains(userId))
                {
                    return $"⚠️ 해당 ID는 관리자가 아닙니다.\nID: {userId}";
                }

                adminList.Remove(userId);
                SaveAdminList(adminList);

                return $"🗑️ 관리자 삭제 완료!\nID: {userId}";
            }
            catch (Exception ex)
            {
                return $"❌ 관리자 삭제 실패: {ex.Message}";
            }
        }

        /// <summary>
        /// PIN 검증 없이 직접 관리자 목록 조회 (채팅 명령어용)
        /// </summary>
        public string GetAdminListDirect()
        {
            try
            {
                var adminList = GetAdminList();

                if (adminList.Count == 0)
                {
                    return "👑 등록된 관리자가 없습니다.";
                }

                var result = "👑 등록된 관리자 목록:\n";
                for (int i = 0; i < adminList.Count; i++)
                {
                    result += $"{i + 1}. {adminList[i]}\n";
                }

                return result.TrimEnd();
            }
            catch (Exception ex)
            {
                return $"❌ 관리자 목록 조회 실패: {ex.Message}";
            }
        }

        /// <summary>
        /// 관리자 여부 확인
        /// </summary>
        public bool IsAdmin(string userId)
        {
            try
            {
                var adminList = GetAdminList();
                return adminList.Contains(userId);
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region 유틸리티 메서드

        /// <summary>
        /// 관리자 수 조회
        /// </summary>
        public int GetAdminCount()
        {
            try
            {
                var adminList = GetAdminList();
                return adminList.Count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 모든 관리자 ID 목록 조회
        /// </summary>
        public List<string> GetAllAdminIds()
        {
            try
            {
                return GetAdminList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// 관리자 데이터 초기화
        /// </summary>
        public string ResetAllAdmins(string pin)
        {
            if (!CheckAdminPin(pin))
            {
                return "❌ 잘못된 PIN입니다.\n관리자 데이터 초기화 실패.";
            }

            try
            {
                if (File.Exists(_adminFilePath))
                {
                    File.Delete(_adminFilePath);
                }
                return "🗑️ 모든 관리자 데이터가 초기화되었습니다.";
            }
            catch (Exception ex)
            {
                return $"❌ 관리자 데이터 초기화 실패: {ex.Message}";
            }
        }

        #endregion

        #region 내부 헬퍼 메서드들

        /// <summary>
        /// 관리자 목록 가져오기
        /// </summary>
        private List<string> GetAdminList()
        {
            try
            {
                if (!File.Exists(_adminFilePath))
                {
                    return new List<string>();
                }

                var lines = File.ReadAllLines(_adminFilePath);
                return lines.Where(line => !string.IsNullOrWhiteSpace(line))
                           .Select(line => line.Trim())
                           .Distinct()
                           .ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"관리자 목록 로드 실패: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 관리자 목록 저장
        /// </summary>
        private void SaveAdminList(List<string> adminList)
        {
            try
            {
                var directory = Path.GetDirectoryName(_adminFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 중복 제거 및 정렬
                var uniqueList = adminList.Where(id => !string.IsNullOrWhiteSpace(id))
                                         .Select(id => id.Trim())
                                         .Distinct()
                                         .OrderBy(id => id)
                                         .ToList();

                File.WriteAllLines(_adminFilePath, uniqueList);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"관리자 목록 저장 실패: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 관리자 파일 백업
        /// </summary>
        public string BackupAdminData()
        {
            try
            {
                if (!File.Exists(_adminFilePath))
                {
                    return "❌ 백업할 관리자 데이터가 없습니다.";
                }

                var backupPath = _adminFilePath + $".backup_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Copy(_adminFilePath, backupPath);

                return $"✅ 관리자 데이터 백업 완료:\n{Path.GetFileName(backupPath)}";
            }
            catch (Exception ex)
            {
                return $"❌ 관리자 데이터 백업 실패: {ex.Message}";
            }
        }

        /// <summary>
        /// 관리자 파일 복원
        /// </summary>
        public string RestoreAdminData(string backupFileName, string pin)
        {
            if (!CheckAdminPin(pin))
            {
                return "❌ 잘못된 PIN입니다.\n관리자 데이터 복원 실패.";
            }

            try
            {
                var directory = Path.GetDirectoryName(_adminFilePath);
                var backupPath = Path.Combine(directory!, backupFileName);

                if (!File.Exists(backupPath))
                {
                    return $"❌ 백업 파일을 찾을 수 없습니다:\n{backupFileName}";
                }

                File.Copy(backupPath, _adminFilePath, true);

                return $"✅ 관리자 데이터 복원 완료:\n{backupFileName}";
            }
            catch (Exception ex)
            {
                return $"❌ 관리자 데이터 복원 실패: {ex.Message}";
            }
        }

        #endregion

        #region 디버그 및 진단

        /// <summary>
        /// 관리자 시스템 상태 정보
        /// </summary>
        public string GetSystemStatus()
        {
            try
            {
                var adminList = GetAdminList();
                var fileExists = File.Exists(_adminFilePath);
                var fileSize = fileExists ? new FileInfo(_adminFilePath).Length : 0;

                return $"📊 관리자 시스템 상태:\n" +
                       $"• 관리자 수: {adminList.Count}명\n" +
                       $"• 파일 존재: {(fileExists ? "✅" : "❌")}\n" +
                       $"• 파일 크기: {fileSize} bytes\n" +
                       $"• 파일 경로: {_adminFilePath}\n" +
                       $"• 현재 PIN: {_currentPin}";
            }
            catch (Exception ex)
            {
                return $"❌ 시스템 상태 조회 실패: {ex.Message}";
            }
        }

        /// <summary>
        /// 관리자 파일 무결성 검사
        /// </summary>
        public string CheckDataIntegrity()
        {
            try
            {
                if (!File.Exists(_adminFilePath))
                {
                    return "✅ 관리자 파일 없음 (정상)";
                }

                var lines = File.ReadAllLines(_adminFilePath);
                var issues = new List<string>();

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        issues.Add($"라인 {i + 1}: 빈 라인");
                    }
                    else if (line != line.Trim())
                    {
                        issues.Add($"라인 {i + 1}: 공백 문자 포함");
                    }
                    else if (line.Length < 5)
                    {
                        issues.Add($"라인 {i + 1}: ID 너무 짧음 ({line})");
                    }
                }

                var duplicates = lines.Where(l => !string.IsNullOrWhiteSpace(l))
                                     .GroupBy(l => l.Trim())
                                     .Where(g => g.Count() > 1)
                                     .Select(g => g.Key);

                foreach (var duplicate in duplicates)
                {
                    issues.Add($"중복 ID: {duplicate}");
                }

                if (issues.Count == 0)
                {
                    return "✅ 관리자 데이터 무결성 검사 통과";
                }
                else
                {
                    return $"⚠️ 발견된 문제점:\n" + string.Join("\n", issues);
                }
            }
            catch (Exception ex)
            {
                return $"❌ 무결성 검사 실패: {ex.Message}";
            }
        }

        #endregion
    }
}