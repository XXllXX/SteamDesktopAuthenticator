using System;
using System.Windows.Forms;
using SteamAuth;

namespace Steam_Desktop_Authenticator
{
    public partial class LoginForm : Form
    {
        public SteamGuardAccount account;
        public LoginType LoginReason;

        public LoginForm(LoginType loginReason = LoginType.Initial, SteamGuardAccount account = null)
        {
            InitializeComponent();
            this.LoginReason = loginReason;
            this.account = account;

            try
            {
                if (this.LoginReason != LoginType.Initial)
                {
                    txtUsername.Text = account.AccountName;
                    txtUsername.Enabled = false;
                }

                if (this.LoginReason == LoginType.Refresh)
                {
                    labelLoginExplanation.Text = "Your Steam credentials have expired. For trade and market confirmations to work properly, please login again.";
                }
            }
            catch (Exception)
            {
                MessageBox.Show("Failed to find your account. Try closing and re-opening SDA.", "Login Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
        }

        public void SetUsername(string username)
        {
            txtUsername.Text = username;
        }

        public string FilterPhoneNumber(string phoneNumber)
        {
            return phoneNumber.Replace("-", "").Replace("(", "").Replace(")", "");
        }

        public bool PhoneNumberOkay(string phoneNumber)
        {
            if (phoneNumber == null || phoneNumber.Length == 0) return false;
            if (phoneNumber[0] != '+') return false;
            return true;
        }

        private void btnSteamLogin_Click(object sender, EventArgs e)
        {
            string username = txtUsername.Text;
            string password = txtPassword.Text;

            if (LoginReason == LoginType.Refresh)
            {
                RefreshLogin(username, password);
                return;
            }

            var userLogin = new UserLogin(username, password);
            LoginResult response = LoginResult.BadCredentials;

            while ((response = userLogin.DoLogin()) != LoginResult.LoginOkay)
            {
                switch (response)
                {
                    case LoginResult.NeedEmail:
                        InputForm emailForm = new InputForm("请输入邮箱验证码:");
                        emailForm.ShowDialog();
                        if (emailForm.Canceled)
                        {
                            this.Close();
                            return;
                        }

                        userLogin.EmailCode = emailForm.txtBox.Text;
                        break;

                    case LoginResult.NeedCaptcha:
                        CaptchaForm captchaForm = new CaptchaForm(userLogin.CaptchaGID);
                        captchaForm.ShowDialog();
                        if (captchaForm.Canceled)
                        {
                            this.Close();
                            return;
                        }

                        userLogin.CaptchaText = captchaForm.CaptchaCode;
                        break;

                    case LoginResult.Need2FA:
                        MessageBox.Show("This account already has a mobile authenticator linked to it.\nRemove the old authenticator from your Steam account before adding a new one.", "Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.Close();
                        return;

                    case LoginResult.BadRSA:
                        MessageBox.Show("Error logging in: Steam returned \"BadRSA\".", "Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.Close();
                        return;

                    case LoginResult.BadCredentials:
                        MessageBox.Show("登陆失败，密码错误。", "Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.Close();
                        return;

                    case LoginResult.TooManyFailedLogins:
                        MessageBox.Show("登录错误，登录失败的次数太多，请稍后再试。", "Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.Close();
                        return;

                    case LoginResult.GeneralFailure:
                        MessageBox.Show("登陆失败，密码错误。", "Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.Close();
                        return;
                }
            }

            //Login succeeded

            SessionData session = userLogin.Session;
            AuthenticatorLinker linker = new AuthenticatorLinker(session);

            AuthenticatorLinker.LinkResult linkResponse = AuthenticatorLinker.LinkResult.GeneralFailure;

            while ((linkResponse = linker.AddAuthenticator()) != AuthenticatorLinker.LinkResult.AwaitingFinalization)
            {
                switch (linkResponse)
                {
                    case AuthenticatorLinker.LinkResult.MustProvidePhoneNumber:
                        string phoneNumber = "";
                        while (!PhoneNumberOkay(phoneNumber))
                        {
                            InputForm phoneNumberForm = new InputForm("请输入您的手机号（带国家区号如： +86 139xxxxxxxx）:");
                            phoneNumberForm.txtBox.Text = "+86";
                            phoneNumberForm.ShowDialog();
                            if (phoneNumberForm.Canceled)
                            {
                                this.Close();
                                return;
                            }

                            phoneNumber = FilterPhoneNumber(phoneNumberForm.txtBox.Text);
                        }
                        linker.PhoneNumber = phoneNumber;
                        break;

                    case AuthenticatorLinker.LinkResult.MustRemovePhoneNumber:
                        linker.PhoneNumber = null;
                        break;

                    case AuthenticatorLinker.LinkResult.MustConfirmEmail:
                        MessageBox.Show("请检查您的电子邮件，并单击Steam发送给您的链接，然后继续。");
                        break;

                    case AuthenticatorLinker.LinkResult.GeneralFailure:
                        MessageBox.Show("添加手机号出错。");
                        this.Close();
                        return;
                }
            }

            Manifest manifest = Manifest.GetManifest();
            string passKey = null;
            if (manifest.Entries.Count == 0)
            {
                passKey = manifest.PromptSetupPassKey("请输入加密密钥。留空或点击取消不加密（非常不安全）。");
            }
            else if (manifest.Entries.Count > 0 && manifest.Encrypted)
            {
                bool passKeyValid = false;
                while (!passKeyValid)
                {
                    InputForm passKeyForm = new InputForm("请输入加密密匙。");
                    passKeyForm.ShowDialog();
                    if (!passKeyForm.Canceled)
                    {
                        passKey = passKeyForm.txtBox.Text;
                        passKeyValid = manifest.VerifyPasskey(passKey);
                        if (!passKeyValid)
                        {
                            MessageBox.Show("该密钥无效，请重新输入。");
                        }
                    }
                    else
                    {
                        this.Close();
                        return;
                    }
                }
            }

            //Save the file immediately; losing this would be bad.
            if (!manifest.SaveAccount(linker.LinkedAccount, passKey != null, passKey))
            {
                manifest.RemoveAccount(linker.LinkedAccount);
                MessageBox.Show("Unable to save mobile authenticator file. The mobile authenticator has not been linked.");
                this.Close();
                return;
            }

            MessageBox.Show("尚未链接移动身份验证程序。在完成身份验证之前，请记住您的救援代码： " + linker.LinkedAccount.RevocationCode);

            AuthenticatorLinker.FinalizeResult finalizeResponse = AuthenticatorLinker.FinalizeResult.GeneralFailure;
            while (finalizeResponse != AuthenticatorLinker.FinalizeResult.Success)
            {
                InputForm smsCodeForm = new InputForm("请输入手机验证码。");
                smsCodeForm.ShowDialog();
                if (smsCodeForm.Canceled)
                {
                    manifest.RemoveAccount(linker.LinkedAccount);
                    this.Close();
                    return;
                }

                InputForm confirmRevocationCode = new InputForm("请输入救援代码，验证是否记住。");
                confirmRevocationCode.ShowDialog();
                if (confirmRevocationCode.txtBox.Text.ToUpper() != linker.LinkedAccount.RevocationCode)
                {
                    MessageBox.Show("救援代码错误， 未完成绑定。");
                    manifest.RemoveAccount(linker.LinkedAccount);
                    this.Close();
                    return;
                }

                string smsCode = smsCodeForm.txtBox.Text;
                finalizeResponse = linker.FinalizeAddAuthenticator(smsCode);

                switch (finalizeResponse)
                {
                    case AuthenticatorLinker.FinalizeResult.BadSMSCode:
                        continue;

                    case AuthenticatorLinker.FinalizeResult.UnableToGenerateCorrectCodes:
                        MessageBox.Show("Unable to generate the proper codes to finalize this authenticator. The authenticator should not have been linked. In the off-chance it was, please write down your revocation code, as this is the last chance to see it: " + linker.LinkedAccount.RevocationCode);
                        manifest.RemoveAccount(linker.LinkedAccount);
                        this.Close();
                        return;

                    case AuthenticatorLinker.FinalizeResult.GeneralFailure:
                        MessageBox.Show("Unable to finalize this authenticator. The authenticator should not have been linked. In the off-chance it was, please write down your revocation code, as this is the last chance to see it: " + linker.LinkedAccount.RevocationCode);
                        manifest.RemoveAccount(linker.LinkedAccount);
                        this.Close();
                        return;
                }
            }

            //Linked, finally. Re-save with FullyEnrolled property.
            manifest.SaveAccount(linker.LinkedAccount, passKey != null, passKey);
            MessageBox.Show("Mobile authenticator successfully linked. Please write down your revocation code: " + linker.LinkedAccount.RevocationCode);
            this.Close();
        }

        /// <summary>
        /// Handles logging in to refresh session data. i.e. changing steam password.
        /// </summary>
        /// <param name="username">Steam username</param>
        /// <param name="password">Steam password</param>
        private async void RefreshLogin(string username, string password)
        {
            long steamTime = await TimeAligner.GetSteamTimeAsync();
            Manifest man = Manifest.GetManifest();

            account.FullyEnrolled = true;

            UserLogin mUserLogin = new UserLogin(username, password);
            LoginResult response = LoginResult.BadCredentials;

            while ((response = mUserLogin.DoLogin()) != LoginResult.LoginOkay)
            {
                switch (response)
                {
                    case LoginResult.NeedCaptcha:
                        CaptchaForm captchaForm = new CaptchaForm(mUserLogin.CaptchaGID);
                        captchaForm.ShowDialog();
                        if (captchaForm.Canceled)
                        {
                            this.Close();
                            return;
                        }

                        mUserLogin.CaptchaText = captchaForm.CaptchaCode;
                        break;

                    case LoginResult.Need2FA:
                        mUserLogin.TwoFactorCode = account.GenerateSteamGuardCodeForTime(steamTime);
                        break;

                    case LoginResult.BadRSA:
                        MessageBox.Show("Error logging in: Steam returned \"BadRSA\".", "Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.Close();
                        return;

                    case LoginResult.BadCredentials:
                        MessageBox.Show("Error logging in: Username or password was incorrect.", "Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.Close();
                        return;

                    case LoginResult.TooManyFailedLogins:
                        MessageBox.Show("Error logging in: Too many failed logins, try again later.", "Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.Close();
                        return;

                    case LoginResult.GeneralFailure:
                        MessageBox.Show("Error logging in: Steam returned \"GeneralFailure\".", "Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.Close();
                        return;
                }
            }

            account.Session = mUserLogin.Session;

            HandleManifest(man, true);
        }

        private void HandleManifest(Manifest man, bool IsRefreshing = false)
        {
            string passKey = null;
            if (man.Entries.Count == 0)
            {
                passKey = man.PromptSetupPassKey("请输入加密密钥。留空或点击取消不加密");
            }
            else if (man.Entries.Count > 0 && man.Encrypted)
            {
                bool passKeyValid = false;
                while (!passKeyValid)
                {
                    InputForm passKeyForm = new InputForm("请输入加密密钥。");
                    passKeyForm.ShowDialog();
                    if (!passKeyForm.Canceled)
                    {
                        passKey = passKeyForm.txtBox.Text;
                        passKeyValid = man.VerifyPasskey(passKey);
                        if (!passKeyValid)
                        {
                            MessageBox.Show("加密密钥错误请重新输入");
                        }
                    }
                    else
                    {
                        this.Close();
                        return;
                    }
                }
            }

            man.SaveAccount(account, passKey != null, passKey);
            if (IsRefreshing)
            {
                MessageBox.Show("Your login session was refreshed.");
            }
            else
            {
                MessageBox.Show("Mobile authenticator successfully linked. Please write down your revocation code: " + account.RevocationCode);
            }
            this.Close();
        }

        private void LoginForm_Load(object sender, EventArgs e)
        {
            if (account != null && account.AccountName != null)
            {
                txtUsername.Text = account.AccountName;
            }
        }

        public enum LoginType
        {
            Initial,
            Refresh
        }
    }
}
