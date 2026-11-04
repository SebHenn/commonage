# Reporting a vulnerability

Please do not report security problems in public issues. Use GitHub's private vulnerability reporting instead:
on the repository page, open **Security** → **Report a vulnerability**. Describe what you found and how to
reproduce it. You will get an answer within a week.

If you have no GitHub account, write to [commonage-project@outlook.com](mailto:commonage-project@outlook.com)
instead, with "Security" in the subject.

Community archives hold unpublished material and personal data of contributors, so please give us time to release
a fix before you publish details.

## Which versions get fixes

Only the newest minor version. A vulnerability in Commonage, or a relevant one in .NET, libvips or the base images,
gets a bug-fix release of it, as a rule within a week of a fix being available. Older minor versions get none, so stay
on the newest one: a server with `COMMONAGE_VERSION=0.3` gets the fixes for 0.3 from `scripts/update.sh`, but does not
move to 0.4 by itself. To hear about releases, follow the
[release feed](https://github.com/SebHenn/commonage/releases.atom) or choose **Watch** → **Releases** on GitHub.

How Commonage is protected, and its known limits, are described in [docs/security.md](docs/security.md).
